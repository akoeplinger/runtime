// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace System.Collections.Concurrent
{
    public partial class ConcurrentQueue<T>
    {
        private Queue<T> _queue;
        private int _activeEnumeratorsOnCurrentCopy;

        /// <summary>
        /// Initializes a new instance of the <see cref="ConcurrentQueue{T}"/> class.
        /// </summary>
        public ConcurrentQueue()
        {
            _queue = new Queue<T>();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ConcurrentQueue{T}"/> class that contains elements copied
        /// from the specified collection.
        /// </summary>
        /// <param name="collection">
        /// The collection whose elements are copied to the new <see cref="ConcurrentQueue{T}"/>.
        /// </param>
        /// <exception cref="ArgumentNullException">The <paramref name="collection"/> argument is null.</exception>
        public ConcurrentQueue(IEnumerable<T> collection)
        {
            _queue = new Queue<T>(collection);
        }

        /// <summary>
        /// If an enumerator is active, clones the queue so the enumerator can continue
        /// iterating the old copy while mutations proceed on the new one.
        /// </summary>
        private void CopyOnWrite()
        {
            if (_activeEnumeratorsOnCurrentCopy > 0)
            {
                _queue = new Queue<T>(_queue);
                _activeEnumeratorsOnCurrentCopy = 0;
            }
        }

        /// <summary>
        /// Gets a value that indicates whether the <see cref="ConcurrentQueue{T}"/> is empty.
        /// </summary>
        /// <value>true if the <see cref="ConcurrentQueue{T}"/> is empty; otherwise, false.</value>
        /// <remarks>
        /// For determining whether the collection contains any items, use of this property is recommended
        /// rather than retrieving the number of items from the <see cref="Count"/> property and comparing it
        /// to 0.  However, as this collection is intended to be accessed concurrently, it may be the case
        /// that another thread will modify the collection after <see cref="IsEmpty"/> returns, thus invalidating
        /// the result.
        /// </remarks>
        public bool IsEmpty => _queue.Count == 0;

        /// <summary>Copies the elements stored in the <see cref="ConcurrentQueue{T}"/> to a new array.</summary>
        /// <returns>A new array containing a snapshot of elements copied from the <see cref="ConcurrentQueue{T}"/>.</returns>
        public T[] ToArray() => _queue.ToArray();

        /// <summary>
        /// Gets the number of elements contained in the <see cref="ConcurrentQueue{T}"/>.
        /// </summary>
        /// <value>The number of elements contained in the <see cref="ConcurrentQueue{T}"/>.</value>
        /// <remarks>
        /// For determining whether the collection contains any items, use of the <see cref="IsEmpty"/>
        /// property is recommended rather than retrieving the number of items from the <see cref="Count"/>
        /// property and comparing it to 0.
        /// </remarks>
        public int Count => _queue.Count;

        /// <summary>
        /// Copies the <see cref="ConcurrentQueue{T}"/> elements to an existing one-dimensional <see
        /// cref="Array">Array</see>, starting at the specified array index.
        /// </summary>
        /// <param name="array">The one-dimensional <see cref="Array">Array</see> that is the
        /// destination of the elements copied from the
        /// <see cref="ConcurrentQueue{T}"/>. The <see cref="Array">Array</see> must have zero-based
        /// indexing.</param>
        /// <param name="index">The zero-based index in <paramref name="array"/> at which copying
        /// begins.</param>
        /// <exception cref="ArgumentNullException"><paramref name="array"/> is a null reference (Nothing in
        /// Visual Basic).</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is less than
        /// zero.</exception>
        /// <exception cref="ArgumentException"><paramref name="index"/> is equal to or greater than the
        /// length of the <paramref name="array"/>
        /// -or- The number of elements in the source <see cref="ConcurrentQueue{T}"/> is greater than the
        /// available space from <paramref name="index"/> to the end of the destination <paramref
        /// name="array"/>.
        /// </exception>
        public void CopyTo(T[] array, int index) => _queue.CopyTo(array, index);

        /// <summary>Returns an enumerator that iterates through the <see cref="ConcurrentQueue{T}"/>.</summary>
        /// <returns>An enumerator for the contents of the <see cref="ConcurrentQueue{T}"/>.</returns>
        /// <remarks>
        /// The enumeration represents a moment-in-time snapshot of the contents of the queue.
        /// It does not reflect any updates to the collection after <see cref="GetEnumerator"/> was called.
        /// </remarks>
        public IEnumerator<T> GetEnumerator()
        {
            _activeEnumeratorsOnCurrentCopy++;
            return new Enumerator(this);
        }

        /// <summary>Enumerator that walks the queue directly. If the queue is mutated during enumeration,
        /// the mutating operation clones the queue first (copy-on-write), so this enumerator continues
        /// walking the old copy.</summary>
        private sealed class Enumerator : IEnumerator<T>
        {
            private readonly ConcurrentQueue<T> _parent;
            private Queue<T>.Enumerator _enumerator;
            private bool _disposed;

            internal Enumerator(ConcurrentQueue<T> parent)
            {
                _parent = parent;
                _enumerator = parent._queue.GetEnumerator();
            }

            public T Current => _enumerator.Current;

            object? IEnumerator.Current => Current;

            public bool MoveNext() => _enumerator.MoveNext();

            public void Reset() => throw new NotSupportedException();

            public void Dispose()
            {
                if (!_disposed)
                {
                    _disposed = true;
                    _enumerator.Dispose();
                    _parent._activeEnumeratorsOnCurrentCopy--;
                }
            }
        }

        /// <summary>Adds an object to the end of the <see cref="ConcurrentQueue{T}"/>.</summary>
        /// <param name="item">
        /// The object to add to the end of the <see cref="ConcurrentQueue{T}"/>.
        /// The value can be a null reference (<see langword="Nothing" /> in Visual Basic) for reference types.
        /// </param>
        public void Enqueue(T item)
        {
            CopyOnWrite();
            _queue.Enqueue(item);
        }

        /// <summary>
        /// Attempts to remove and return the object at the beginning of the <see
        /// cref="ConcurrentQueue{T}"/>.
        /// </summary>
        /// <param name="result">
        /// When this method returns, if the operation was successful, <paramref name="result"/> contains the
        /// object removed. If no object was available to be removed, the value is unspecified.
        /// </param>
        /// <returns>
        /// true if an element was removed and returned from the beginning of the
        /// <see cref="ConcurrentQueue{T}"/> successfully; otherwise, false.
        /// </returns>
        public bool TryDequeue([MaybeNullWhen(false)] out T result)
        {
            CopyOnWrite();
            return _queue.TryDequeue(out result);
        }

        /// <summary>
        /// Attempts to return an object from the beginning of the <see cref="ConcurrentQueue{T}"/>
        /// without removing it.
        /// </summary>
        /// <param name="result">
        /// When this method returns, <paramref name="result"/> contains an object from
        /// the beginning of the <see cref="ConcurrentQueue{T}"/> or default(T)
        /// if the operation failed.
        /// </param>
        /// <returns>true if and object was returned successfully; otherwise, false.</returns>
        /// <remarks>
        /// For determining whether the collection contains any items, use of the <see cref="IsEmpty"/>
        /// property is recommended rather than peeking.
        /// </remarks>
        public bool TryPeek([MaybeNullWhen(false)] out T result) => _queue.TryPeek(out result);

        /// <summary>
        /// Removes all objects from the <see cref="ConcurrentQueue{T}"/>.
        /// </summary>
        public void Clear()
        {
            CopyOnWrite();
            _queue.Clear();
        }
    }
}
