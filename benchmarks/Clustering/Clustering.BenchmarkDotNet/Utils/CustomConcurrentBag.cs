using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace System.Collections.Concurrent
{
    [DebuggerDisplay("Count = {Count}")]
    public class CustomConcurrentBag<T> : IProducerConsumerCollection<T>, IEnumerable<T>, IEnumerable, ICollection, IReadOnlyCollection<T>
    {
        private readonly ThreadLocal<CustomConcurrentBag<T>.WorkStealingQueue> _locals;
        private volatile CustomConcurrentBag<T>.WorkStealingQueue _workStealingQueues;
        private long _emptyToNonEmptyListTransitionCount;

        public CustomConcurrentBag()
        {
            _locals = new ThreadLocal<CustomConcurrentBag<T>.WorkStealingQueue>();
        }

        public CustomConcurrentBag(IEnumerable<T> collection)
        {
            if (collection == null)
                throw new ArgumentNullException(nameof(collection));
            _locals = new ThreadLocal<CustomConcurrentBag<T>.WorkStealingQueue>();
            CustomConcurrentBag<T>.WorkStealingQueue workStealingQueue = GetCurrentThreadWorkStealingQueue(true);
            foreach (T obj in collection)
                workStealingQueue.LocalPush(obj, ref _emptyToNonEmptyListTransitionCount);
        }

        public void Add(T item)
        {
            GetCurrentThreadWorkStealingQueue(true).LocalPush(item, ref _emptyToNonEmptyListTransitionCount);
        }

        bool IProducerConsumerCollection<T>.TryAdd(T item)
        {
            Add(item);
            return true;
        }

        public bool TryTake([MaybeNullWhen(false)] out T result)
        {
            CustomConcurrentBag<T>.WorkStealingQueue workStealingQueue = GetCurrentThreadWorkStealingQueue(false);
            if (workStealingQueue == null || !workStealingQueue.TryLocalPop(out result))
                return TrySteal(out result, true);
            return true;
        }

        public bool TryPeek([MaybeNullWhen(false)] out T result)
        {
            CustomConcurrentBag<T>.WorkStealingQueue workStealingQueue = GetCurrentThreadWorkStealingQueue(false);
            if (workStealingQueue == null || !workStealingQueue.TryLocalPeek(out result))
                return TrySteal(out result, false);
            return true;
        }

        private CustomConcurrentBag<T>.WorkStealingQueue GetCurrentThreadWorkStealingQueue(
          bool forceCreate)
        {
            CustomConcurrentBag<T>.WorkStealingQueue workStealingQueue = _locals.Value;
            if (workStealingQueue != null)
                return workStealingQueue;
            if (!forceCreate)
                return null;
            return CreateWorkStealingQueueForCurrentThread();
        }

        private CustomConcurrentBag<T>.WorkStealingQueue CreateWorkStealingQueueForCurrentThread()
        {
            lock (GlobalQueuesLock)
            {
                CustomConcurrentBag<T>.WorkStealingQueue workStealingQueues = _workStealingQueues;
                CustomConcurrentBag<T>.WorkStealingQueue workStealingQueue = workStealingQueues != null ? GetUnownedWorkStealingQueue() : null;
                if (workStealingQueue == null)
                    _workStealingQueues = workStealingQueue = new CustomConcurrentBag<T>.WorkStealingQueue(workStealingQueues);
                _locals.Value = workStealingQueue;
                return workStealingQueue;
            }
        }

        private CustomConcurrentBag<T>.WorkStealingQueue GetUnownedWorkStealingQueue()
        {
            int currentManagedThreadId = Environment.CurrentManagedThreadId;
            for (CustomConcurrentBag<T>.WorkStealingQueue workStealingQueue = _workStealingQueues; workStealingQueue != null; workStealingQueue = workStealingQueue._nextQueue)
            {
                if (workStealingQueue._ownerThreadId == currentManagedThreadId)
                    return workStealingQueue;
            }
            return null;
        }

        private bool TrySteal(out T result, bool take)
        {
            long num;
            do
            {
                num = Interlocked.Read(ref _emptyToNonEmptyListTransitionCount);
                CustomConcurrentBag<T>.WorkStealingQueue workStealingQueue = GetCurrentThreadWorkStealingQueue(false);
                if (workStealingQueue == null ? TryStealFromTo(_workStealingQueues, null, out result, take) : TryStealFromTo(workStealingQueue._nextQueue, null, out result, take) || TryStealFromTo(_workStealingQueues, workStealingQueue, out result, take))
                    return true;
            }
            while (Interlocked.Read(ref _emptyToNonEmptyListTransitionCount) != num);
            return false;
        }

        private bool TryStealFromTo(
          CustomConcurrentBag<T>.WorkStealingQueue startInclusive,
          CustomConcurrentBag<T>.WorkStealingQueue endExclusive,
          [MaybeNullWhen(false)] out T result,
          bool take)
        {
            for (CustomConcurrentBag<T>.WorkStealingQueue workStealingQueue = startInclusive; workStealingQueue != endExclusive; workStealingQueue = workStealingQueue._nextQueue)
            {
                if (workStealingQueue.TrySteal(out result, take))
                    return true;
            }
            result = default(T);
            return false;
        }
        public int CopyTo(ArrayPool<T> arrayPool, out T[] array)
        {
            if (arrayPool == null)
                throw new ArgumentNullException(nameof(arrayPool));
            if (_workStealingQueues == null)
            {
                array = Array.Empty<T>();
                return 0;
            }

            bool lockTaken = false;
            try
            {
                FreezeBag(ref lockTaken);
                int dangerousCount = DangerousCount;

                try
                {
                    array = arrayPool.Rent(dangerousCount);
                    return CopyFromEachQueueToArray(array, 0);
                }
                catch (ArrayTypeMismatchException ex)
                {
                    throw new InvalidCastException(ex.Message, ex);
                }
            }
            finally
            {
                UnfreezeBag(lockTaken);
            }
        }

        public void CopyTo(T[] array, int index)
        {
            if (array == null)
                throw new ArgumentNullException(nameof(array));
            if (index < 0)
                throw new ArgumentOutOfRangeException(nameof(index));
            if (_workStealingQueues == null)
                return;
            bool lockTaken = false;
            try
            {
                FreezeBag(ref lockTaken);
                int dangerousCount = DangerousCount;
                if (index > array.Length - dangerousCount)
                    throw new ArgumentException(nameof(index));
                try
                {
                    CopyFromEachQueueToArray(array, index);
                }
                catch (ArrayTypeMismatchException ex)
                {
                    throw new InvalidCastException(ex.Message, ex);
                }
            }
            finally
            {
                UnfreezeBag(lockTaken);
            }
        }

        private int CopyFromEachQueueToArray(T[] array, int index)
        {
            int arrayIndex = index;
            for (CustomConcurrentBag<T>.WorkStealingQueue workStealingQueue = _workStealingQueues; workStealingQueue != null; workStealingQueue = workStealingQueue._nextQueue)
                arrayIndex += workStealingQueue.DangerousCopyTo(array, arrayIndex);
            return arrayIndex - index;
        }

        void ICollection.CopyTo(Array array, int index)
        {
            T[] array1 = array as T[];
            if (array1 != null)
            {
                CopyTo(array1, index);
            }
            else
            {
                if (array == null)
                    throw new ArgumentNullException(nameof(array));
                ToArray().CopyTo(array, index);
            }
        }

        public T[] ToArray()
        {
            if (_workStealingQueues != null)
            {
                bool lockTaken = false;
                try
                {
                    FreezeBag(ref lockTaken);
                    int dangerousCount = DangerousCount;
                    if (dangerousCount > 0)
                    {
                        T[] array = new T[dangerousCount];
                        CopyFromEachQueueToArray(array, 0);
                        return array;
                    }
                }
                finally
                {
                    UnfreezeBag(lockTaken);
                }
            }
            return Array.Empty<T>();
        }

        public void Clear()
        {
            if (_workStealingQueues == null)
                return;
            CustomConcurrentBag<T>.WorkStealingQueue workStealingQueue1 = GetCurrentThreadWorkStealingQueue(false);
            if (workStealingQueue1 != null)
            {
                workStealingQueue1.LocalClear();
                if (workStealingQueue1._nextQueue == null && workStealingQueue1 == _workStealingQueues)
                    return;
            }
            bool lockTaken = false;
            try
            {
                FreezeBag(ref lockTaken);
                for (CustomConcurrentBag<T>.WorkStealingQueue workStealingQueue2 = _workStealingQueues; workStealingQueue2 != null; workStealingQueue2 = workStealingQueue2._nextQueue)
                {
                    do
                        ;
                    while (workStealingQueue2.TrySteal(out T result, true));
                }
            }
            finally
            {
                UnfreezeBag(lockTaken);
            }
        }

        public IEnumerator<T> GetEnumerator()
        {
            return new CustomConcurrentBag<T>.Enumerator(ToArray());
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public int Count
        {
            get
            {
                if (_workStealingQueues == null)
                    return 0;
                bool lockTaken = false;
                try
                {
                    FreezeBag(ref lockTaken);
                    return DangerousCount;
                }
                finally
                {
                    UnfreezeBag(lockTaken);
                }
            }
        }

        private int DangerousCount
        {
            get
            {
                int num = 0;
                for (CustomConcurrentBag<T>.WorkStealingQueue workStealingQueue = _workStealingQueues; workStealingQueue != null; workStealingQueue = workStealingQueue._nextQueue)
                    checked { num += workStealingQueue.DangerousCount; }
                return num;
            }
        }

        public bool IsEmpty
        {
            get
            {
                CustomConcurrentBag<T>.WorkStealingQueue workStealingQueue1 = GetCurrentThreadWorkStealingQueue(false);
                if (workStealingQueue1 != null)
                {
                    if (!workStealingQueue1.IsEmpty)
                        return false;
                    if (workStealingQueue1._nextQueue == null && workStealingQueue1 == _workStealingQueues)
                        return true;
                }
                bool lockTaken = false;
                try
                {
                    FreezeBag(ref lockTaken);
                    for (CustomConcurrentBag<T>.WorkStealingQueue workStealingQueue2 = _workStealingQueues; workStealingQueue2 != null; workStealingQueue2 = workStealingQueue2._nextQueue)
                    {
                        if (!workStealingQueue2.IsEmpty)
                            return false;
                    }
                }
                finally
                {
                    UnfreezeBag(lockTaken);
                }
                return true;
            }
        }

        bool ICollection.IsSynchronized => false;

        object ICollection.SyncRoot => throw new NotSupportedException();

        private object GlobalQueuesLock => _locals;

        private void FreezeBag(ref bool lockTaken)
        {
            Monitor.Enter(GlobalQueuesLock, ref lockTaken);
            CustomConcurrentBag<T>.WorkStealingQueue workStealingQueues = _workStealingQueues;
            for (CustomConcurrentBag<T>.WorkStealingQueue workStealingQueue = workStealingQueues; workStealingQueue != null; workStealingQueue = workStealingQueue._nextQueue)
                Monitor.Enter(workStealingQueue, ref workStealingQueue._frozen);
            Interlocked.MemoryBarrier();
            for (CustomConcurrentBag<T>.WorkStealingQueue workStealingQueue = workStealingQueues; workStealingQueue != null; workStealingQueue = workStealingQueue._nextQueue)
            {
                if (workStealingQueue._currentOp != 0)
                {
                    SpinWait spinWait = new SpinWait();
                    do
                    {
                        spinWait.SpinOnce();
                    }
                    while (workStealingQueue._currentOp != 0);
                }
            }
        }

        private void UnfreezeBag(bool lockTaken)
        {
            if (!lockTaken)
                return;
            for (CustomConcurrentBag<T>.WorkStealingQueue workStealingQueue = _workStealingQueues; workStealingQueue != null; workStealingQueue = workStealingQueue._nextQueue)
            {
                if (workStealingQueue._frozen)
                {
                    workStealingQueue._frozen = false;
                    Monitor.Exit(workStealingQueue);
                }
            }
            Monitor.Exit(GlobalQueuesLock);
        }

        private sealed class WorkStealingQueue
        {
            private volatile T[] _array = new T[32];
            private volatile int _mask = 31;
            private volatile int _headIndex;
            private volatile int _tailIndex;
            private int _addTakeCount;
            private int _stealCount;
            internal volatile int _currentOp;
            internal bool _frozen;
            internal readonly CustomConcurrentBag<T>.WorkStealingQueue _nextQueue;
            internal readonly int _ownerThreadId;

            internal WorkStealingQueue(CustomConcurrentBag<T>.WorkStealingQueue nextQueue)
            {
                _ownerThreadId = Environment.CurrentManagedThreadId;
                _nextQueue = nextQueue;
            }

            internal bool IsEmpty => _headIndex - _tailIndex >= 0;

            internal void LocalPush(T item, ref long emptyToNonEmptyListTransitionCount)
            {
                bool lockTaken = false;
                try
                {
                    Interlocked.Exchange(ref _currentOp, 1);
                    int num1 = _tailIndex;
                    if (num1 == int.MaxValue)
                    {
                        _currentOp = 0;
                        lock (this)
                        {
                            _headIndex &= _mask;
                            _tailIndex = (num1 &= _mask);
                            Interlocked.Exchange(ref _currentOp, 1);
                        }
                    }
                    int headIndex1 = _headIndex;
                    if (!_frozen && headIndex1 - (num1 - 1) < 0 && num1 - (headIndex1 + _mask) < 0)
                    {
                        _array[num1 & _mask] = item;
                        _tailIndex = num1 + 1;
                    }
                    else
                    {
                        _currentOp = 0;
                        Monitor.Enter(this, ref lockTaken);
                        int headIndex2 = _headIndex;
                        int num2 = num1 - headIndex2;
                        if (num2 >= _mask)
                        {
                            T[] objArray = new T[_array.Length << 1];
                            int num3 = headIndex2 & _mask;
                            if (num3 == 0)
                            {
                                Array.Copy(_array, 0, objArray, 0, _array.Length);
                            }
                            else
                            {
                                Array.Copy(_array, num3, objArray, 0, _array.Length - num3);
                                Array.Copy(_array, 0, objArray, _array.Length - num3, num3);
                            }
                            _array = objArray;
                            _headIndex = 0;
                            _tailIndex = num1 = num2;
                            _mask = _mask << 1 | 1;
                        }
                        _array[num1 & _mask] = item;
                        _tailIndex = num1 + 1;
                        if (num2 == 0)
                            Interlocked.Increment(ref emptyToNonEmptyListTransitionCount);
                        _addTakeCount -= _stealCount;
                        _stealCount = 0;
                    }
                    checked { ++_addTakeCount; }
                }
                finally
                {
                    _currentOp = 0;
                    if (lockTaken)
                        Monitor.Exit(this);
                }
            }

            internal void LocalClear()
            {
                lock (this)
                {
                    if (_headIndex - _tailIndex >= 0)
                        return;
                    _headIndex = _tailIndex = 0;
                    _addTakeCount = _stealCount = 0;
                    Array.Clear(_array, 0, _array.Length);
                }
            }

            internal bool TryLocalPop([MaybeNullWhen(false)] out T result)
            {
                int tailIndex = _tailIndex;
                if (_headIndex - tailIndex >= 0)
                {
                    result = default(T);
                    return false;
                }
                bool lockTaken = false;
                try
                {
                    _currentOp = 2;
                    int num;
                    Interlocked.Exchange(ref _tailIndex, num = tailIndex - 1);
                    if (!_frozen && _headIndex - num < 0)
                    {
                        int index = num & _mask;
                        result = _array[index];
                        _array[index] = default(T);
                        --_addTakeCount;
                        return true;
                    }
                    _currentOp = 0;
                    Monitor.Enter(this, ref lockTaken);
                    if (_headIndex - num <= 0)
                    {
                        int index = num & _mask;
                        result = _array[index];
                        _array[index] = default(T);
                        --_addTakeCount;
                        return true;
                    }
                    _tailIndex = num + 1;
                    result = default(T);
                    return false;
                }
                finally
                {
                    _currentOp = 0;
                    if (lockTaken)
                        Monitor.Exit(this);
                }
            }

            internal bool TryLocalPeek([MaybeNullWhen(false)] out T result)
            {
                int tailIndex = _tailIndex;
                if (_headIndex - tailIndex < 0)
                {
                    lock (this)
                    {
                        if (_headIndex - tailIndex < 0)
                        {
                            result = _array[tailIndex - 1 & _mask];
                            return true;
                        }
                    }
                }
                result = default(T);
                return false;
            }

            internal bool TrySteal([MaybeNullWhen(false)] out T result, bool take)
            {
                lock (this)
                {
                    int headIndex = _headIndex;
                    if (take)
                    {
                        if (headIndex - (_tailIndex - 2) >= 0 && _currentOp == 1)
                        {
                            SpinWait spinWait = new SpinWait();
                            do
                            {
                                spinWait.SpinOnce();
                            }
                            while (_currentOp == 1);
                        }
                        Interlocked.Exchange(ref _headIndex, headIndex + 1);
                        if (headIndex < _tailIndex)
                        {
                            int index = headIndex & _mask;
                            result = _array[index];
                            _array[index] = default(T);
                            ++_stealCount;
                            return true;
                        }
                        _headIndex = headIndex;
                    }
                    else if (headIndex < _tailIndex)
                    {
                        result = _array[headIndex & _mask];
                        return true;
                    }
                }
                result = default(T);
                return false;
            }

            internal int DangerousCopyTo(T[] array, int arrayIndex)
            {
                int headIndex = _headIndex;
                int dangerousCount = DangerousCount;
                for (int index = arrayIndex + dangerousCount - 1; index >= arrayIndex; --index)
                    array[index] = _array[headIndex++ & _mask];
                return dangerousCount;
            }

            internal int DangerousCount => _addTakeCount - _stealCount;
        }

        private sealed class Enumerator : IEnumerator<T>, IDisposable, IEnumerator
        {
            private readonly T[] _array;
            [AllowNull]
            private T _current;
            private int _index;

            public Enumerator(T[] array)
            {
                _array = array;
            }

            public bool MoveNext()
            {
                if (_index < _array.Length)
                {
                    _current = _array[_index++];
                    return true;
                }
                _index = _array.Length + 1;
                return false;
            }

            public T Current => _current;

            object IEnumerator.Current
            {
                get
                {
                    if (_index == 0 || _index == _array.Length + 1)
                        throw new InvalidOperationException();
                    return Current;
                }
            }

            public void Reset()
            {
                _index = 0;
                _current = default(T);
            }

            public void Dispose()
            {
            }
        }
    }
}
