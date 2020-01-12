﻿using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Clustering.BenchmarkDotNet
{
    public sealed class ConcurrentList<T> : ThreadSafeList<T>
    {
        private static readonly int[] Sizes;
        private static readonly int[] Counts;
        private readonly T[][] _array;
        private int _count;
        private int _fuzzyCount;

        private int _index;

        static ConcurrentList()
        {
            Sizes = new int[32];
            Counts = new int[32];

            var size = 1;
            var count = 1;

            for (var i = 0; i < Sizes.Length; i++)
            {
                Sizes[i] = size;
                Counts[i] = count;

                if (i < Sizes.Length - 1)
                {
                    size *= 2;
                    count += size;
                }
            }
        }

        public ConcurrentList()
        {
            _array = new T[32][];
        }

        public ConcurrentList(IEnumerable<T> enumerable)
        {
            _array = new T[32][];

            foreach (var item in enumerable)
                Add(item);
        }

        public override T this[int index]
        {
            get
            {
                if (index < 0 || index >= _count)
                    throw new ArgumentOutOfRangeException("index");

                var arrayIndex = GetArrayIndex(index + 1);

                if (arrayIndex > 0)
                    index -= (int) Math.Pow(2, arrayIndex) - 1;

                return _array[arrayIndex][index];
            }
        }

        public override int Count => _count;

        #region "Protected methods"

        protected override bool IsSynchronizedBase => false;

        #endregion

        public override void Add(T element)
        {
            var index = Interlocked.Increment(ref _index) - 1;
            var adjustedIndex = index;

            var arrayIndex = GetArrayIndex(index + 1);

            if (arrayIndex > 0)
                adjustedIndex -= Counts[arrayIndex - 1];

            if (_array[arrayIndex] == null)
            {
                var arrayLength = Sizes[arrayIndex];
                Interlocked.CompareExchange(ref _array[arrayIndex], new T[arrayLength], null);
            }

            _array[arrayIndex][adjustedIndex] = element;

            var count = _count;
            var fuzzyCount = Interlocked.Increment(ref _fuzzyCount);

            if (fuzzyCount == index + 1)
                Interlocked.CompareExchange(ref _count, fuzzyCount, count);
        }

        public override void CopyTo(T[] array, int index)
        {
            if (array == null)
                throw new ArgumentNullException("array");

            var count = _count;

            if (array.Length - index < count)
                throw new ArgumentException("There is not enough available space in the destination array.");

            var arrayIndex = 0;
            var elementsRemaining = count;

            while (elementsRemaining > 0)
            {
                var source = _array[arrayIndex++];
                var elementsToCopy = Math.Min(source.Length, elementsRemaining);
                var startIndex = count - elementsRemaining;

                Array.Copy(source, 0, array, startIndex, elementsToCopy);

                elementsRemaining -= elementsToCopy;
            }
        }

        private static int GetArrayIndex(int count)
        {
            var arrayIndex = 0;

            if ((count & 0xFFFF0000) != 0)
            {
                count >>= 16;
                arrayIndex |= 16;
            }

            if ((count & 0xFF00) != 0)
            {
                count >>= 8;
                arrayIndex |= 8;
            }

            if ((count & 0xF0) != 0)
            {
                count >>= 4;
                arrayIndex |= 4;
            }

            if ((count & 0xC) != 0)
            {
                count >>= 2;
                arrayIndex |= 2;
            }

            if ((count & 0x2) != 0)
            {
                count >>= 1;
                arrayIndex |= 1;
            }

            return arrayIndex;
        }
    }

    public abstract class ThreadSafeList<T> : IList<T>, IList
    {
        public abstract T this[int index] { get; }

        public abstract int Count { get; }

        public abstract void Add(T item);

        public virtual int IndexOf(T item)
        {
            IEqualityComparer<T> comparer = EqualityComparer<T>.Default;

            var count = Count;

            for (var i = 0; i < count; i++)
                if (comparer.Equals(item, this[i]))
                    return i;

            return -1;
        }

        public virtual bool Contains(T item)
        {
            return IndexOf(item) != -1;
        }

        public abstract void CopyTo(T[] array, int arrayIndex);

        public IEnumerator<T> GetEnumerator()
        {
            var count = Count;

            for (var i = 0; i < count; i++)
            {
                if(this[i] == null)
                    Debugger.Break();

                yield return this[i];
            }
        }

        #region "Protected methods"

        protected abstract bool IsSynchronizedBase { get; }

        protected virtual void CopyToBase(Array array, int arrayIndex)
        {
            for (var i = 0; i < Count; ++i)
                array.SetValue(this[i], arrayIndex + i);
        }

        protected virtual int AddBase(object value)
        {
            Add((T) value);

            return Count - 1;
        }

        #endregion

        #region "Explicit interface implementations"

        T IList<T>.this[int index]
        {
            get => this[index];
            set => throw new NotSupportedException();
        }

        void IList<T>.Insert(int index, T item)
        {
            throw new NotSupportedException();
        }

        void IList<T>.RemoveAt(int index)
        {
            throw new NotSupportedException();
        }

        bool ICollection<T>.IsReadOnly => false;

        void ICollection<T>.Clear()
        {
            throw new NotSupportedException();
        }

        bool ICollection<T>.Remove(T item)
        {
            throw new NotSupportedException();
        }

        bool IList.IsFixedSize => false;

        bool IList.IsReadOnly => false;

        object IList.this[int index]
        {
            get => this[index];
            set => throw new NotSupportedException();
        }

        void IList.RemoveAt(int index)
        {
            throw new NotSupportedException();
        }

        void IList.Remove(object value)
        {
            throw new NotSupportedException();
        }

        void IList.Insert(int index, object value)
        {
            throw new NotSupportedException();
        }

        int IList.IndexOf(object value)
        {
            return IndexOf((T) value);
        }

        void IList.Clear()
        {
            throw new NotSupportedException();
        }

        bool IList.Contains(object value)
        {
            return ((IList) this).IndexOf(value) != -1;
        }

        int IList.Add(object value)
        {
            return AddBase(value);
        }

        bool ICollection.IsSynchronized => IsSynchronizedBase;

        object ICollection.SyncRoot => null;

        void ICollection.CopyTo(Array array, int arrayIndex)
        {
            CopyToBase(array, arrayIndex);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        #endregion
    }
}