// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace System.Collections.Concurrent
{
    [DebuggerTypeProxy(typeof(IDictionaryDebugView<,>))]
    [DebuggerDisplay("Count = {Count}")]
    public partial class ConcurrentDictionary<TKey, TValue> : IDictionary<TKey, TValue>, IDictionary, IReadOnlyDictionary<TKey, TValue> where TKey : notnull
    {
        private const int DefaultCapacity = 31;

        private static int DefaultConcurrencyLevel => Environment.ProcessorCount;

        /// <summary>Throws a KeyNotFoundException.</summary>
        /// <remarks>Separate from ThrowHelper to avoid boxing at call site while reusing this generic instantiation.</remarks>
        [DoesNotReturn]
        private static void ThrowKeyNotFoundException(TKey key) =>
            throw new KeyNotFoundException(SR.Format(SR.Arg_KeyNotFoundWithKey, key.ToString()));

        /// <summary>Computes an initial capacity to use based on an initial seed collection.</summary>
        /// <param name="collection">The collection with which to initially populate this dictionary.</param>
        /// <returns>The capacity to use.</returns>
        /// <remarks>
        /// Growing is expensive, and we don't know if the caller plans to add additional items beyond this
        /// initial collection, so we use the maximum of the collection's size and the default capacity. That way,
        /// the initial capacity selected isn't pessimized by seeding it with a collection that happens to be
        /// smaller.
        /// </remarks>
        private static int GetCapacityFromCollection(IEnumerable<KeyValuePair<TKey, TValue>> collection) =>
            collection switch
            {
                ICollection<KeyValuePair<TKey, TValue>> c => Math.Max(DefaultCapacity, c.Count),
                IReadOnlyCollection<KeyValuePair<TKey, TValue>> rc => Math.Max(DefaultCapacity, rc.Count),
                _ => DefaultCapacity,
            };

        /// <summary>
        /// Initializes a new instance of the <see cref="ConcurrentDictionary{TKey,TValue}"/>
        /// class that is empty, has the default concurrency level, has the default initial capacity, and
        /// uses the default comparer for the key type.
        /// </summary>
        public ConcurrentDictionary()
            : this(DefaultConcurrencyLevel, DefaultCapacity, growLockArray: true, null) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="ConcurrentDictionary{TKey,TValue}"/>
        /// class that is empty, has the specified concurrency level and capacity, and uses the default
        /// comparer for the key type.
        /// </summary>
        /// <param name="concurrencyLevel">The estimated number of threads that will update the
        /// <see cref="ConcurrentDictionary{TKey,TValue}"/> concurrently, or -1 to indicate a default value.</param>
        /// <param name="capacity">The initial number of elements that the <see cref="ConcurrentDictionary{TKey,TValue}"/> can contain.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="concurrencyLevel"/> is less than 1.</exception>
        /// <exception cref="ArgumentOutOfRangeException"> <paramref name="capacity"/> is less than 0.</exception>
        public ConcurrentDictionary(int concurrencyLevel, int capacity)
            : this(concurrencyLevel, capacity, growLockArray: false, null) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="ConcurrentDictionary{TKey,TValue}"/>
        /// class that contains elements copied from the specified <see cref="IEnumerable{T}"/>, has the default concurrency
        /// level, has the default initial capacity, and uses the default comparer for the key type.
        /// </summary>
        /// <param name="collection">The <see
        /// cref="IEnumerable{T}"/> whose elements are copied to the new <see cref="ConcurrentDictionary{TKey,TValue}"/>.</param>
        /// <exception cref="ArgumentNullException"><paramref name="collection"/> is a null reference (Nothing in Visual Basic).</exception>
        /// <exception cref="ArgumentException"><paramref name="collection"/> contains one or more duplicate keys.</exception>
        public ConcurrentDictionary(IEnumerable<KeyValuePair<TKey, TValue>> collection)
            : this(DefaultConcurrencyLevel, collection, null) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="ConcurrentDictionary{TKey,TValue}"/>
        /// class that is empty, has the specified concurrency level and capacity, and uses the specified
        /// <see cref="IEqualityComparer{TKey}"/>.
        /// </summary>
        /// <param name="comparer">The <see cref="IEqualityComparer{TKey}"/> implementation to use when comparing keys.</param>
        public ConcurrentDictionary(IEqualityComparer<TKey>? comparer)
            : this(DefaultConcurrencyLevel, DefaultCapacity, growLockArray: true, comparer) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="ConcurrentDictionary{TKey,TValue}"/>
        /// class that contains elements copied from the specified <see cref="IEnumerable"/>, has the default concurrency
        /// level, has the default initial capacity, and uses the specified <see cref="IEqualityComparer{TKey}"/>.
        /// </summary>
        /// <param name="collection">The <see cref="IEnumerable{T}"/> whose elements are copied to the new <see cref="ConcurrentDictionary{TKey,TValue}"/>.</param>
        /// <param name="comparer">The <see cref="IEqualityComparer{TKey}"/> implementation to use when comparing keys.</param>
        /// <exception cref="ArgumentNullException"><paramref name="collection"/> is a null reference (Nothing in Visual Basic).</exception>
        public ConcurrentDictionary(IEnumerable<KeyValuePair<TKey, TValue>> collection, IEqualityComparer<TKey>? comparer)
            : this(DefaultConcurrencyLevel, GetCapacityFromCollection(collection), comparer)
        {
            ArgumentNullException.ThrowIfNull(collection);

            InitializeFromCollection(collection);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ConcurrentDictionary{TKey,TValue}"/>
        /// class that contains elements copied from the specified <see cref="IEnumerable"/>,
        /// has the specified concurrency level, has the specified initial capacity, and uses the specified
        /// <see cref="IEqualityComparer{TKey}"/>.
        /// </summary>
        /// <param name="concurrencyLevel">
        /// The estimated number of threads that will update the <see cref="ConcurrentDictionary{TKey,TValue}"/> concurrently, or -1 to indicate a default value.
        /// </param>
        /// <param name="collection">The <see cref="IEnumerable{T}"/> whose elements are copied to the new
        /// <see cref="ConcurrentDictionary{TKey,TValue}"/>.</param>
        /// <param name="comparer">The <see cref="IEqualityComparer{TKey}"/> implementation to use when comparing keys.</param>
        /// <exception cref="ArgumentNullException"><paramref name="collection"/> is a null reference (Nothing in Visual Basic).</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="concurrencyLevel"/> is less than 1.</exception>
        /// <exception cref="ArgumentException"><paramref name="collection"/> contains one or more duplicate keys.</exception>
        public ConcurrentDictionary(int concurrencyLevel, IEnumerable<KeyValuePair<TKey, TValue>> collection, IEqualityComparer<TKey>? comparer)
            : this(concurrencyLevel, GetCapacityFromCollection(collection), growLockArray: false, comparer)
        {
            ArgumentNullException.ThrowIfNull(collection);

            InitializeFromCollection(collection);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ConcurrentDictionary{TKey,TValue}"/>
        /// class that is empty, has the specified concurrency level, has the specified initial capacity, and
        /// uses the specified <see cref="IEqualityComparer{TKey}"/>.
        /// </summary>
        /// <param name="concurrencyLevel">The estimated number of threads that will update the <see cref="ConcurrentDictionary{TKey,TValue}"/> concurrently, or -1 to indicate a default value.</param>
        /// <param name="capacity">The initial number of elements that the <see cref="ConcurrentDictionary{TKey,TValue}"/> can contain.</param>
        /// <param name="comparer">The <see cref="IEqualityComparer{TKey}"/> implementation to use when comparing keys.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="concurrencyLevel"/> is less than 1. -or- <paramref name="capacity"/> is less than 0.</exception>
        public ConcurrentDictionary(int concurrencyLevel, int capacity, IEqualityComparer<TKey>? comparer)
            : this(concurrencyLevel, capacity, growLockArray: false, comparer) { }

        /// <summary>
        /// Determines whether the <see cref="ConcurrentDictionary{TKey, TValue}"/> contains the specified key.
        /// </summary>
        /// <param name="key">The key to locate in the <see cref="ConcurrentDictionary{TKey, TValue}"/>.</param>
        /// <returns>true if the <see cref="ConcurrentDictionary{TKey, TValue}"/> contains an element with the specified key; otherwise, false.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> is a null reference (Nothing in Visual Basic).</exception>
        public bool ContainsKey(TKey key) => TryGetValue(key, out _);

        /// <summary>
        /// Attempts to add the specified key and value to the <see cref="ConcurrentDictionary{TKey, TValue}"/>.
        /// </summary>
        /// <param name="key">The key of the element to add.</param>
        /// <param name="value">The value of the element to add. The value can be a null reference (Nothing
        /// in Visual Basic) for reference types.</param>
        /// <returns>
        /// true if the key/value pair was added to the <see cref="ConcurrentDictionary{TKey, TValue}"/> successfully; otherwise, false.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> is null reference (Nothing in Visual Basic).</exception>
        /// <exception cref="OverflowException">The <see cref="ConcurrentDictionary{TKey, TValue}"/> contains too many elements.</exception>
        public bool TryAdd(TKey key, TValue value)
        {
            if (key is null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
            }

            return TryAddInternal(key, value, updateIfExists: false, out _);
        }

        /// <summary>
        /// Attempts to remove and return the value with the specified key from the <see cref="ConcurrentDictionary{TKey, TValue}"/>.
        /// </summary>
        /// <param name="key">The key of the element to remove and return.</param>
        /// <param name="value">
        /// When this method returns, <paramref name="value"/> contains the object removed from the
        /// <see cref="ConcurrentDictionary{TKey,TValue}"/> or the default value of <typeparamref
        /// name="TValue"/> if the operation failed.
        /// </param>
        /// <returns>true if an object was removed successfully; otherwise, false.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> is a null reference (Nothing in Visual Basic).</exception>
        public bool TryRemove(TKey key, [MaybeNullWhen(false)] out TValue value)
        {
            if (key is null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
            }

            return TryRemoveInternal(key, out value, matchValue: false, default);
        }

        /// <summary>Removes a key and value from the dictionary.</summary>
        /// <param name="item">The <see cref="KeyValuePair{TKey,TValue}"/> representing the key and value to remove.</param>
        /// <returns>
        /// true if the key and value represented by <paramref name="item"/> are successfully
        /// found and removed; otherwise, false.
        /// </returns>
        /// <remarks>
        /// Both the specified key and value must match the entry in the dictionary for it to be removed.
        /// The key is compared using the dictionary's comparer (or the default comparer for <typeparamref name="TKey"/>
        /// if no comparer was provided to the dictionary when it was constructed).  The value is compared using the
        /// default comparer for <typeparamref name="TValue"/>.
        /// </remarks>
        /// <exception cref="ArgumentNullException">
        /// The <see cref="KeyValuePair{TKey, TValue}.Key"/> property of <paramref name="item"/> is a null reference.
        /// </exception>
        public bool TryRemove(KeyValuePair<TKey, TValue> item)
        {
            if (item.Key is null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.item, ExceptionResource.ConcurrentDictionary_ItemKeyIsNull);
            }

            return TryRemoveInternal(item.Key, out _, matchValue: true, item.Value);
        }

        /// <summary>
        /// Gets the number of key/value pairs contained in the <see
        /// cref="ConcurrentDictionary{TKey,TValue}"/>.
        /// </summary>
        /// <exception cref="OverflowException">The dictionary contains too many
        /// elements.</exception>
        /// <value>The number of key/value pairs contained in the <see
        /// cref="ConcurrentDictionary{TKey,TValue}"/>.</value>
        /// <remarks>Count has snapshot semantics and represents the number of items in the <see
        /// cref="ConcurrentDictionary{TKey,TValue}"/>
        /// at the moment when Count was accessed.</remarks>
        public int Count
        {
            get
            {
                int locksAcquired = 0;
                try
                {
                    AcquireAllLocks(ref locksAcquired);

                    return GetCountNoLocks();
                }
                finally
                {
                    ReleaseLocks(locksAcquired);
                }
            }
        }

        /// <summary>
        /// Copies the elements of the <see cref="ICollection{T}"/> to an array of type <see cref="KeyValuePair{TKey,TValue}"/>,
        /// starting at the specified array index.
        /// </summary>
        /// <param name="array">
        /// The one-dimensional array of type <see cref="KeyValuePair{TKey,TValue}"/> that is the destination of the <see
        /// cref="KeyValuePair{TKey,TValue}"/> elements copied from the <see  cref="ICollection"/>. The array must have zero-based indexing.
        /// </param>
        /// <param name="index">The zero-based index in <paramref name="array"/> at which copying begins.</param>
        /// <exception cref="ArgumentNullException"><paramref name="array"/> is a null reference (Nothing in Visual Basic).</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is less than 0.</exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="index"/> is equal to or greater than the length of the <paramref name="array"/>. -or- The number of
        /// elements in the source <see cref="ICollection"/> is greater than the available space from <paramref name="index"/> to
        /// the end of the destination <paramref name="array"/>.
        /// </exception>
        void ICollection<KeyValuePair<TKey, TValue>>.CopyTo(KeyValuePair<TKey, TValue>[] array, int index)
        {
            ArgumentNullException.ThrowIfNull(array);

            ArgumentOutOfRangeException.ThrowIfNegative(index);

            int locksAcquired = 0;
            try
            {
                AcquireAllLocks(ref locksAcquired);

                int count = GetCountNoLocks();
                if (array.Length - count < index)
                {
                    throw new ArgumentException(SR.ConcurrentDictionary_ArrayNotLargeEnough);
                }

                CopyToPairs(array, index);
            }
            finally
            {
                ReleaseLocks(locksAcquired);
            }
        }

        /// <summary>
        /// Copies the key and value pairs stored in the <see cref="ConcurrentDictionary{TKey,TValue}"/> to a
        /// new array.
        /// </summary>
        /// <returns>A new array containing a snapshot of key and value pairs copied from the <see cref="ConcurrentDictionary{TKey,TValue}"/>.
        /// </returns>
        public KeyValuePair<TKey, TValue>[] ToArray()
        {
            int locksAcquired = 0;
            try
            {
                AcquireAllLocks(ref locksAcquired);

                int count = GetCountNoLocks();
                if (count == 0)
                {
                    return [];
                }

                var array = new KeyValuePair<TKey, TValue>[count];
                CopyToPairs(array, 0);
                return array;
            }
            finally
            {
                ReleaseLocks(locksAcquired);
            }
        }

        /// <summary>
        /// Gets a snapshot containing all the keys in the <see cref="ConcurrentDictionary{TKey,TValue}"/>.
        /// </summary>
        /// <remarks>The property returns a copy of all the keys. It's not kept in sync with <see cref="ConcurrentDictionary{TKey,TValue}"/>.</remarks>
        public ICollection<TKey> Keys => GetKeys();

        /// <inheritdoc cref="Keys"/>
        IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => GetKeys();

        /// <summary>
        /// Gets a snapshot containing all the values in the <see cref="ConcurrentDictionary{TKey,TValue}"/>.
        /// </summary>
        /// <remarks>The property returns a copy of all the values. It's not kept in sync with <see cref="ConcurrentDictionary{TKey,TValue}"/>.</remarks>
        public ICollection<TValue> Values => GetValues();

        /// <inheritdoc cref="Values"/>
        IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => GetValues();

        #region IDictionary<TKey,TValue> members

        void IDictionary<TKey, TValue>.Add(TKey key, TValue value)
        {
            if (!TryAdd(key, value))
            {
                throw new ArgumentException(SR.ConcurrentDictionary_KeyAlreadyExisted);
            }
        }

        bool IDictionary<TKey, TValue>.Remove(TKey key) => TryRemove(key, out _);

        #endregion

        #region ICollection<KeyValuePair<TKey,TValue>> Members

        void ICollection<KeyValuePair<TKey, TValue>>.Add(KeyValuePair<TKey, TValue> keyValuePair) => ((IDictionary<TKey, TValue>)this).Add(keyValuePair.Key, keyValuePair.Value);

        bool ICollection<KeyValuePair<TKey, TValue>>.Contains(KeyValuePair<TKey, TValue> keyValuePair) =>
            TryGetValue(keyValuePair.Key, out TValue? value) &&
            EqualityComparer<TValue>.Default.Equals(value, keyValuePair.Value);

        bool ICollection<KeyValuePair<TKey, TValue>>.IsReadOnly => false;

        bool ICollection<KeyValuePair<TKey, TValue>>.Remove(KeyValuePair<TKey, TValue> keyValuePair) =>
            TryRemove(keyValuePair);

        #endregion

        #region IEnumerable Members

        IEnumerator IEnumerable.GetEnumerator() => ((ConcurrentDictionary<TKey, TValue>)this).GetEnumerator();

        #endregion

        #region IDictionary Members

        /// <summary>
        /// Adds the specified key and value to the dictionary.
        /// </summary>
        /// <param name="key">The object to use as the key.</param>
        /// <param name="value">The object to use as the value.</param>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> is a null reference
        /// (Nothing in Visual Basic).</exception>
        /// <exception cref="OverflowException">The dictionary contains too many
        /// elements.</exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="key"/> is of a type that is not assignable to the key type <typeparamref
        /// name="TKey"/> of the <see cref="ConcurrentDictionary{TKey,TValue}"/>. -or-
        /// <paramref name="value"/> is of a type that is not assignable to <typeparamref name="TValue"/>,
        /// the type of values in the <see cref="ConcurrentDictionary{TKey,TValue}"/>.
        /// -or- A value with the same key already exists in the <see
        /// cref="ConcurrentDictionary{TKey,TValue}"/>.
        /// </exception>
        void IDictionary.Add(object key, object? value)
        {
            if (key is null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
            }

            if (key is not TKey)
            {
                throw new ArgumentException(SR.ConcurrentDictionary_TypeOfKeyIncorrect);
            }

            ThrowIfInvalidObjectValue(value);

            ((IDictionary<TKey, TValue>)this).Add((TKey)key, (TValue)value!);
        }

        /// <summary>
        /// Gets whether the <see cref="IDictionary"/> contains an
        /// element with the specified key.
        /// </summary>
        /// <param name="key">The key to locate in the <see
        /// cref="IDictionary"/>.</param>
        /// <returns>true if the <see cref="IDictionary"/> contains
        /// an element with the specified key; otherwise, false.</returns>
        /// <exception cref="ArgumentNullException"> <paramref name="key"/> is a null reference
        /// (Nothing in Visual Basic).</exception>
        bool IDictionary.Contains(object key)
        {
            if (key is null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
            }

            return key is TKey tkey && ContainsKey(tkey);
        }

        /// <summary>Provides an <see cref="IDictionaryEnumerator"/> for the
        /// <see cref="IDictionary"/>.</summary>
        /// <returns>An <see cref="IDictionaryEnumerator"/> for the <see
        /// cref="IDictionary"/>.</returns>
        IDictionaryEnumerator IDictionary.GetEnumerator() => new DictionaryEnumerator(this);

        /// <summary>
        /// Gets a value indicating whether the <see
        /// cref="IDictionary"/> has a fixed size.
        /// </summary>
        /// <value>true if the <see cref="IDictionary"/> has a
        /// fixed size; otherwise, false. For <see
        /// cref="ConcurrentDictionary{TKey,TValue}"/>, this property always
        /// returns false.</value>
        bool IDictionary.IsFixedSize => false;

        /// <summary>
        /// Gets a value indicating whether the <see
        /// cref="IDictionary"/> is read-only.
        /// </summary>
        /// <value>true if the <see cref="IDictionary"/> is
        /// read-only; otherwise, false. For <see
        /// cref="ConcurrentDictionary{TKey,TValue}"/>, this property always
        /// returns false.</value>
        bool IDictionary.IsReadOnly => false;

        /// <summary>
        /// Gets an <see cref="ICollection"/> containing the keys of the <see
        /// cref="IDictionary"/>.
        /// </summary>
        /// <value>An <see cref="ICollection"/> containing the keys of the <see
        /// cref="IDictionary"/>.</value>
        ICollection IDictionary.Keys => GetKeys();

        /// <summary>
        /// Removes the element with the specified key from the <see
        /// cref="IDictionary"/>.
        /// </summary>
        /// <param name="key">The key of the element to remove.</param>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> is a null reference
        /// (Nothing in Visual Basic).</exception>
        void IDictionary.Remove(object key)
        {
            if (key is null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
            }

            if (key is TKey tkey)
            {
                TryRemove(tkey, out _);
            }
        }

        /// <summary>
        /// Gets an <see cref="ICollection"/> containing the values in the <see
        /// cref="IDictionary"/>.
        /// </summary>
        /// <value>An <see cref="ICollection"/> containing the values in the <see
        /// cref="IDictionary"/>.</value>
        ICollection IDictionary.Values => GetValues();

        /// <summary>
        /// Gets or sets the value associated with the specified key.
        /// </summary>
        /// <param name="key">The key of the value to get or set.</param>
        /// <value>The value associated with the specified key, or a null reference (Nothing in Visual Basic)
        /// if <paramref name="key"/> is not in the dictionary or <paramref name="key"/> is of a type that is
        /// not assignable to the key type <typeparamref name="TKey"/> of the <see
        /// cref="ConcurrentDictionary{TKey,TValue}"/>.</value>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> is a null reference
        /// (Nothing in Visual Basic).</exception>
        /// <exception cref="ArgumentException">
        /// A value is being assigned, and <paramref name="key"/> is of a type that is not assignable to the
        /// key type <typeparamref name="TKey"/> of the <see
        /// cref="ConcurrentDictionary{TKey,TValue}"/>. -or- A value is being
        /// assigned, and <paramref name="key"/> is of a type that is not assignable to the value type
        /// <typeparamref name="TValue"/> of the <see
        /// cref="ConcurrentDictionary{TKey,TValue}"/>
        /// </exception>
        object? IDictionary.this[object key]
        {
            get
            {
                if (key is null)
                {
                    ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
                }

                if (key is TKey tkey && TryGetValue(tkey, out TValue? value))
                {
                    return value;
                }

                return null;
            }
            set
            {
                if (key is null)
                {
                    ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
                }

                if (key is not TKey)
                {
                    throw new ArgumentException(SR.ConcurrentDictionary_TypeOfKeyIncorrect);
                }

                ThrowIfInvalidObjectValue(value);

                ((ConcurrentDictionary<TKey, TValue>)this)[(TKey)key] = (TValue)value!;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ThrowIfInvalidObjectValue(object? value)
        {
            if (value is not null)
            {
                if (value is not TValue)
                {
                    ThrowHelper.ThrowArgumentException(ExceptionResource.ConcurrentDictionary_TypeOfValueIncorrect);
                }
            }
            else if (default(TValue) is not null)
            {
                ThrowHelper.ThrowArgumentException(ExceptionResource.ConcurrentDictionary_TypeOfValueIncorrect);
            }
        }

        #endregion

        #region ICollection Members

        /// <summary>
        /// Copies the elements of the <see cref="ICollection"/> to an array, starting
        /// at the specified array index.
        /// </summary>
        /// <param name="array">The one-dimensional array that is the destination of the elements copied from
        /// the <see cref="ICollection"/>. The array must have zero-based
        /// indexing.</param>
        /// <param name="index">The zero-based index in <paramref name="array"/> at which copying
        /// begins.</param>
        /// <exception cref="ArgumentNullException"><paramref name="array"/> is a null reference
        /// (Nothing in Visual Basic).</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is less than
        /// 0.</exception>
        /// <exception cref="ArgumentException"><paramref name="index"/> is equal to or greater than
        /// the length of the <paramref name="array"/>. -or- The number of elements in the source <see
        /// cref="ICollection"/>
        /// is greater than the available space from <paramref name="index"/> to the end of the destination
        /// <paramref name="array"/>.</exception>
        void ICollection.CopyTo(Array array, int index)
        {
            ArgumentNullException.ThrowIfNull(array);
            ArgumentOutOfRangeException.ThrowIfNegative(index);

            int locksAcquired = 0;
            try
            {
                AcquireAllLocks(ref locksAcquired);

                int count = GetCountNoLocks();
                if (array.Length - count < index)
                {
                    throw new ArgumentException(SR.ConcurrentDictionary_ArrayNotLargeEnough);
                }

                // To be consistent with the behavior of ICollection.CopyTo() in Dictionary<TKey,TValue>,
                // we recognize three types of target arrays:
                //    - an array of KeyValuePair<TKey, TValue> structs
                //    - an array of DictionaryEntry structs
                //    - an array of objects

                if (array is KeyValuePair<TKey, TValue>[] pairs)
                {
                    CopyToPairs(pairs, index);
                    return;
                }

                if (array is DictionaryEntry[] entries)
                {
                    CopyToEntries(entries, index);
                    return;
                }

                if (array is object[] objects)
                {
                    CopyToObjects(objects, index);
                    return;
                }

                throw new ArgumentException(SR.ConcurrentDictionary_ArrayIncorrectType, nameof(array));
            }
            finally
            {
                ReleaseLocks(locksAcquired);
            }
        }

        bool ICollection.IsSynchronized => false;

        object ICollection.SyncRoot => throw new NotSupportedException(SR.ConcurrentCollection_SyncRoot_NotSupported);

        #endregion

        /// <summary>
        /// A private class to represent enumeration over the dictionary that implements the
        /// IDictionaryEnumerator interface.
        /// </summary>
        private sealed class DictionaryEnumerator : IDictionaryEnumerator
        {
            private readonly IEnumerator<KeyValuePair<TKey, TValue>> _enumerator; // Enumerator over the dictionary.

            internal DictionaryEnumerator(ConcurrentDictionary<TKey, TValue> dictionary) => _enumerator = dictionary.GetEnumerator();

            public DictionaryEntry Entry => new DictionaryEntry(_enumerator.Current.Key, _enumerator.Current.Value);

            public object Key => _enumerator.Current.Key;

            public object? Value => _enumerator.Current.Value;

            public object Current => Entry;

            public bool MoveNext() => _enumerator.MoveNext();

            public void Reset() => _enumerator.Reset();
        }

        /// <summary>
        /// Provides a type that may be used to perform operations on a <see cref="ConcurrentDictionary{TKey, TValue}"/>
        /// using a <typeparamref name="TAlternateKey"/> as a key instead of a <typeparamref name="TKey"/>.
        /// </summary>
        /// <typeparam name="TAlternateKey">The alternate type of a key for performing lookups.</typeparam>
        public readonly partial struct AlternateLookup<TAlternateKey> where TAlternateKey : notnull, allows ref struct
        {
            /// <summary>Gets the <see cref="ConcurrentDictionary{TKey, TValue}"/> against which this instance performs operations.</summary>
            public ConcurrentDictionary<TKey, TValue> Dictionary { get; }

            /// <summary>Gets or sets the value associated with the specified alternate key.</summary>
            /// <param name="key">The alternate key of the value to get or set.</param>
            /// <value>
            /// The value associated with the specified alternate key. If the specified alternate key is not found, a get operation throws
            /// a <see cref="KeyNotFoundException"/>, and a set operation creates a new element with the specified key.
            /// </value>
            /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
            /// <exception cref="KeyNotFoundException">The property is retrieved and alternate key does not exist in the collection.</exception>
            public TValue this[TAlternateKey key]
            {
                get => TryGetValue(key, out TValue? value) ? value : throw new KeyNotFoundException();
                set => TryAdd(key, value, updateIfExists: true, out _);
            }

            /// <summary>Determines whether the <see cref="ConcurrentDictionary{TKey, TValue}"/> contains the specified alternate key.</summary>
            /// <param name="key">The alternate key to check.</param>
            /// <returns><see langword="true"/> if the key is in the dictionary; otherwise, <see langword="false"/>.</returns>
            /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
            public bool ContainsKey(TAlternateKey key) =>
                TryGetValue(key, out _);

            /// <summary>Attempts to add the specified key and value to the dictionary.</summary>
            /// <param name="key">The alternate key of the element to add.</param>
            /// <param name="value">The value of the element to add.</param>
            /// <returns>true if the key/value pair was added to the dictionary successfully; otherwise, false.</returns>
            /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
            public bool TryAdd(TAlternateKey key, TValue value) =>
                TryAdd(key, value, updateIfExists: false, out _);

            /// <summary>Gets the value associated with the specified alternate key.</summary>
            /// <param name="key">The alternate key of the value to get.</param>
            /// <param name="value">
            /// When this method returns, contains the value associated with the specified key, if the key is found;
            /// otherwise, the default value for the type of the value parameter.
            /// </param>
            /// <returns><see langword="true"/> if an entry was found; otherwise, <see langword="false"/>.</returns>
            /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
            public bool TryGetValue(TAlternateKey key, [MaybeNullWhen(false)] out TValue value) =>
                TryGetValue(key, out _, out value);

            /// <summary>
            /// Removes the value with the specified alternate key from the <see cref="Dictionary{TKey, TValue}"/>,
            /// and copies the element to the value parameter.
            /// </summary>
            /// <param name="key">The alternate key of the element to remove.</param>
            /// <param name="value">The removed element.</param>
            /// <returns>true if the element is successfully found and removed; otherwise, false.</returns>
            /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
            public bool TryRemove(TAlternateKey key, [MaybeNullWhen(false)] out TValue value) =>
                TryRemove(key, out _, out value);
        }
    }

    internal sealed class IDictionaryDebugView<TKey, TValue> where TKey : notnull
    {
        private readonly IDictionary<TKey, TValue> _dictionary;

        public IDictionaryDebugView(IDictionary<TKey, TValue> dictionary)
        {
            if (dictionary is null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.dictionary);
            }

            _dictionary = dictionary;
        }

        [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
        public DebugViewDictionaryItem<TKey, TValue>[] Items
        {
            get
            {
                var keyValuePairs = new KeyValuePair<TKey, TValue>[_dictionary.Count];
                _dictionary.CopyTo(keyValuePairs, 0);
                var items = new DebugViewDictionaryItem<TKey, TValue>[keyValuePairs.Length];
                for (int i = 0; i < items.Length; i++)
                {
                    items[i] = new DebugViewDictionaryItem<TKey, TValue>(keyValuePairs[i]);
                }
                return items;
            }
        }
    }
}
