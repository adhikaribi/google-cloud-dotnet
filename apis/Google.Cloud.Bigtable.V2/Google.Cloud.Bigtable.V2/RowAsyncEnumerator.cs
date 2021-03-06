﻿// Copyright 2017 Google Inc. All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using Google.Protobuf;
using Grpc.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using static Google.Cloud.Bigtable.V2.BigtableClient;
using static Google.Cloud.Bigtable.V2.ReadRowsResponse.Types;

namespace Google.Cloud.Bigtable.V2
{
    /// <summary>
    /// Asynchronous enumerator which merges cell chunks from the <see cref="ReadRowsStream"/> into
    /// <see cref="Row"/> instances.
    /// </summary>
    internal sealed class RowAsyncEnumerator : IAsyncEnumerator<Row>
    {
        private CellInfo _currentCell;
        private RowMergeState _rowMergeState = NewRow.Instance;
        private IEnumerator<Row> _singleResponseRowEnumerator;
        private readonly ReadRowsStream _stream;

        public RowAsyncEnumerator(ReadRowsStream stream)
        {
            _stream = stream;
        }

        public Row Current { get; private set; }

        /// <summary>
        /// For testing purposes
        /// </summary>
        public bool HadSplitCell { get; private set; }

        public void Dispose() => _stream.GrpcCall.Dispose();

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            // Try to walk to the next row for the current response being processed.
            while (_singleResponseRowEnumerator?.MoveNext() != true)
            {
                // If not possible, dispose the old enumerator and create a new one from the
                // next response, if any.
                _singleResponseRowEnumerator?.Dispose();
                if (await _stream.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _singleResponseRowEnumerator = MergeResponseChunks(_stream.ResponseStream.Current);
                }
                else
                {
                    _singleResponseRowEnumerator = null;
                    break;
                }
            }

            // If the current response's enumerator has produced a row, this enumerator should produce
            // that same row.
            if (_singleResponseRowEnumerator != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Current = _singleResponseRowEnumerator.Current;
                return true;
            }

            if (IsRowInProgress &&
                _stream.GrpcCall.GetStatus().StatusCode != StatusCode.Cancelled)
            {
                // TODO: Is there something we could/should do here? Stream prematurely closed
                //       with a row in progress
            }

            return false;

            IEnumerator<Row> MergeResponseChunks(ReadRowsResponse response)
            {
                foreach (var chunk in response.Chunks)
                {
                    if (chunk.ResetRow)
                    {
                        Reset();
                        continue;
                    }

                    _rowMergeState = _rowMergeState.HandleChunk(this, chunk);

                    if (chunk.CommitRow)
                    {
                        _rowMergeState = _rowMergeState.CommitRow(this);

                        // TODO: Capture response.LastScannedRowKey here for smart retries
                        yield return _currentCell.Row;
                    }
                }
            }
        }

        public bool IsRowInProgress => _rowMergeState != NewRow.Instance;

        private void AddCompletedCellToRow(ByteString value)
        {
            var cell = new Cell
            {
                Value = value,
                TimestampMicros = _currentCell.Timestamp,
                Labels = { _currentCell.Labels }
            };
            Assert(_currentCell.Column != null, "NewCell has no qualifier");
            _currentCell.Column.Cells.Add(cell);
        }

        private void Assert(bool condition, string message)
        {
            Debug.Assert(message != null, "Provide an assert message");

            if (!condition)
            {
                Reset();
                throw new InvalidOperationException(message);
            }
        }

        private void Reset() => _currentCell = new CellInfo();

        private struct CellInfo
        {
            public Column Column;
            public Family Family;
            public IEnumerable<string> Labels;
            public Row Row;
            public long Timestamp;
            public List<byte> ValueAccumulator;
            public int ValueSizeExpected;
            public int ValueSizeRemaining;
        }

        /// <summary>
        /// Base class for all of the state machine's internal states.
        /// </summary>
        private abstract class RowMergeState
        {
            public abstract RowMergeState HandleChunk(RowAsyncEnumerator owner, CellChunk chunk);

            public virtual RowMergeState CommitRow(RowAsyncEnumerator owner)
            {
                owner.Assert(false, $"Cannot commit a row from {GetType().Name}");
                return this;
            }
        }

        /// <summary>
        /// The default state when the state machine is awaiting a chunk to start a new row. It will update the current
        /// cell info for the new row and delegate to <see cref="NewCell"/> to process the first cell.
        /// </summary>
        private sealed class NewRow : RowMergeState
        {
            public static readonly NewRow Instance = new NewRow();

            private NewRow() { }

            public override RowMergeState HandleChunk(RowAsyncEnumerator owner, CellChunk chunk)
            {
                owner.Assert(chunk.RowKey != null && !chunk.RowKey.IsEmpty, "NewRow must have a rowKey");
                owner.Assert(chunk.FamilyName != null, "NewRow must have a family");
                owner.Assert(chunk.Qualifier != null, "NewRow must have a qualifier");
                owner.Assert(
                    owner._currentCell.Row == null || BigtableByteString.Compare(owner._currentCell.Row.Key, chunk.RowKey) < 0,
                    "NewRow key must be greater than the previous row's");

                // WARNING: owner._currentCell is a struct value. Do not extract as a local (which will make a copy).
                owner._currentCell.Row = new Row { Key = chunk.RowKey };
                owner._currentCell.Family = null;
                owner._currentCell.Column = null;
                owner._currentCell.Timestamp = 0;
                owner._currentCell.Labels = null;

                // auto transition
                return NewCell.Instance.HandleChunk(owner, chunk);
            }
        }

        /// <summary>
        /// A state that represents a cell boundary in a row.
        /// Transitions back to <see cref="NewCell"/> if a processed chunk contains a full cell value or to
        /// <see cref="CellInProgress"/> if it contains a partial value.
        /// </summary>
        private sealed class NewCell : RowMergeState
        {
            public static readonly NewCell Instance = new NewCell();

            private NewCell() { }

            public override RowMergeState HandleChunk(RowAsyncEnumerator owner, CellChunk chunk)
            {
                // update name & make sure it changed
                bool idChanged = false;
                if (chunk.FamilyName != null)
                {
                    if (chunk.FamilyName != owner._currentCell.Family?.Name)
                    {
                        idChanged = true;
                        owner._currentCell.Family = new Family { Name = chunk.FamilyName };
                        owner._currentCell.Row.Families.Add(owner._currentCell.Family);
                    }
                    owner.Assert(chunk.Qualifier != null, "NewCell has a familyName, but no qualifier");
                }

                if (chunk.Qualifier != null && chunk.Qualifier != owner._currentCell.Column?.Qualifier)
                {
                    idChanged = true;
                    owner._currentCell.Column = new Column { Qualifier = chunk.Qualifier };
                    owner.Assert(
                        owner._currentCell.Family != null,
                        "NewCell has a qualifier, but no familyName");
                    owner._currentCell.Family.Columns.Add(owner._currentCell.Column);
                }

                idChanged = idChanged ||
                    owner._currentCell.Timestamp != chunk.TimestampMicros;
                owner._currentCell.Timestamp = chunk.TimestampMicros;

                idChanged = idChanged ||
                    !chunk.Labels.SequenceEqual(owner._currentCell.Labels ?? Enumerable.Empty<string>());
                owner._currentCell.Labels = chunk.Labels;

                owner.Assert(idChanged, "NewCell must have a new identifier");
                owner.Assert(chunk.Value != null, "NewCell must have a value");

                // calculate cell size
                owner._currentCell.ValueSizeExpected = chunk.ValueSize > 0 ? chunk.ValueSize : chunk.Value.Length;
                owner._currentCell.ValueSizeRemaining = owner._currentCell.ValueSizeExpected - chunk.Value.Length;
                
                if (owner._currentCell.ValueSizeRemaining != 0)
                {
                    ref var accumulator = ref owner._currentCell.ValueAccumulator;
                    if (accumulator == null)
                    {
                        accumulator = new List<byte>(owner._currentCell.ValueSizeExpected);
                    }
                    else
                    {
                        Debug.Assert(accumulator.Count == 0);
                        accumulator.Capacity = Math.Max(owner._currentCell.ValueSizeExpected, accumulator.Capacity);
                    }

                    owner._currentCell.ValueAccumulator.AddRange(chunk.Value);
                    return CellInProgress.Instance;
                }
                
                owner.AddCompletedCellToRow(chunk.Value);
                return NewCell.Instance;
            }
            
            public override RowMergeState CommitRow(RowAsyncEnumerator owner)
            {
                Debug.Assert(owner._currentCell.ValueSizeRemaining == 0);
                return NewRow.Instance;
            }
        }

        /// <summary>
        /// A state that represents a continuation value from the previous cell's value, which is incomplete.
        /// Transitions to <see cref="NewCell"/> if a processed chunk completes the value or back to
        /// <see cref="CellInProgress"/> if it doesn't.
        /// </summary>
        private sealed class CellInProgress : RowMergeState
        {
            public static readonly CellInProgress Instance = new CellInProgress();

            private CellInProgress() { }

            public override RowMergeState HandleChunk(RowAsyncEnumerator owner, CellChunk chunk)
            {
                owner.HadSplitCell = true;
                owner.Assert(chunk.FamilyName == null, "CellInProgress can't have a family");
                owner.Assert(chunk.Qualifier == null, "CellInProgress can't have a qualifier");
                owner.Assert(chunk.TimestampMicros == 0, "CellInProgress can't have a timestamp");
                owner.Assert(chunk.Labels.Count == 0, "CellInProgress can't have labels");
                owner.Assert(chunk.Value != null, "CellInProgress must have a value");

                owner._currentCell.ValueSizeRemaining -= chunk.Value.Length;

                bool isLast = chunk.ValueSize == 0;
                if (isLast)
                {
                    owner.Assert(
                        owner._currentCell.ValueSizeRemaining == 0,
                        $"CellInProgress is last, but is missing {owner._currentCell.ValueSizeRemaining} bytes");
                }

                ref var accumulator = ref owner._currentCell.ValueAccumulator;
                accumulator.AddRange(chunk.Value);

                if (!isLast)
                {
                    return CellInProgress.Instance;    
                }
                
                owner.AddCompletedCellToRow(ByteString.CopyFrom(accumulator.ToArray()));
                accumulator.Clear();
                return NewCell.Instance;
            }
        }
    }
}
