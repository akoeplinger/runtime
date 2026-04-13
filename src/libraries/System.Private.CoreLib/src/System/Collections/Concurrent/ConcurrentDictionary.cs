// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace System.Collections.Concurrent
{
    /// <summary>Represents a thread-safe collection of keys and values.</summary>
    /// <typeparam name="TKey">The type of the keys in the dictionary.</typeparam>
    /// <typeparam name="TValue">The type of the values in the dictionary.</typeparam>
    /// <remarks>
    /// All public and protected members of <see cref="ConcurrentDictionary{TKey,TValue}"/> are thread-safe and may be used
    /// concurrently from multiple threads.
    /// </remarks>
    public partial class ConcurrentDictionary<TKey, TValue>
    {
        /// <summary>Internal tables of the dictionary.</summary>
        /// <remarks>
        /// When using <see cref="_tables"/>, we must read the volatile _tables field into a local variable:
        /// it is set to a new table on each table resize. Volatile.Reads on array elements then ensure that
        /// we have a copy of the reference to tables._buckets[bucketNo]: this protects us from reading fields
        /// ('_hashcode', '_key', '_value' and '_next') of different instances.
        /// </remarks>
        private volatile Tables _tables;
        /// <summary>The maximum number of elements per lock before a resize operation is triggered.</summary>
        private int _budget;
        /// <summary>Whether to dynamically increase the size of the striped lock.</summary>
        private readonly bool _growLockArray;
        /// <summary>Whether a non-null comparer in <see cref="Tables._comparer"/> is the default comparer.</summary>
        /// <remarks>
        /// This is only used for reference types. It lets us use the key's GetHashCode directly rather than going indirectly
        /// through the comparer.  It can't be used for Equals, as the key might implement IEquatable and employ different
        /// equality semantics than the virtual Equals, however unlikely that may be. This field enables us to save an
        /// interface dispatch when using the default comparer with a non-string reference type key, at the expense of an
        /// extra branch when using a custom comparer with a reference type key.
        /// </remarks>
        private readonly bool _comparerIsDefaultForClasses;
        /// <summary>The initial size of the _buckets array.</summary>
        /// <remarks>
        /// We store this to retain the initially specified growing behavior of the _buckets array even after clearing the collection.
        /// </remarks>
        private readonly int _initialCapacity;

        /// <summary>
        /// The maximum size of the striped lock that will not be exceeded when locks are automatically
        /// added as the dictionary grows.
        /// </summary>
        /// <remarks>
        /// The user is allowed to exceed this limit by passing
        /// a concurrency level larger than MaxLockNumber into the constructor.
        /// </remarks>
        private const int MaxLockNumber = 1024;

        internal ConcurrentDictionary(int concurrencyLevel, int capacity, bool growLockArray, IEqualityComparer<TKey>? comparer)
        {
            if (concurrencyLevel <= 0)
            {
                if (concurrencyLevel != -1)
                {
                    throw new ArgumentOutOfRangeException(nameof(concurrencyLevel), SR.ConcurrentDictionary_ConcurrencyLevelMustBePositiveOrNegativeOne);
                }

                concurrencyLevel = DefaultConcurrencyLevel;
            }

            ArgumentOutOfRangeException.ThrowIfNegative(capacity);

            // The capacity should be at least as large as the concurrency level. Otherwise, we would have locks that don't guard
            // any buckets.  We also want it to be a prime.
            if (capacity < concurrencyLevel)
            {
                capacity = concurrencyLevel;
            }
            capacity = HashHelpers.GetPrime(capacity);

            var locks = new object[concurrencyLevel];
            locks[0] = locks; // reuse array as the first lock object just to avoid an additional allocation
            for (int i = 1; i < locks.Length; i++)
            {
                locks[i] = new object();
            }

            var countPerLock = new int[locks.Length];
            var buckets = new VolatileNode[capacity];

            // For reference types, we always want to store a comparer instance, either the one provided, or if
            // one wasn't provided, the default (accessing EqualityComparer<TKey>.Default with shared generics
            // on every dictionary access can add measurable overhead).  For value types, if no comparer is provided,
            // or if the default is provided, we'd prefer to use EqualityComparer<TKey>.Default.Equals/GetHashCode
            // on every use, enabling the JIT to devirtualize and possibly inline the operation.
            if (typeof(TKey).IsValueType)
            {
                if (comparer is not null && // first check for null to avoid forcing default comparer instantiation unnecessarily
                    ReferenceEquals(comparer, EqualityComparer<TKey>.Default))
                {
                    comparer = null;
                }
            }
            else
            {
                comparer ??= EqualityComparer<TKey>.Default;

                // Special-case EqualityComparer<string>.Default, StringComparer.Ordinal, and StringComparer.OrdinalIgnoreCase.
                // We use a non-randomized comparer for improved perf, falling back to a randomized comparer if the
                // hash buckets become unbalanced.
                if (typeof(TKey) == typeof(string) &&
                    NonRandomizedStringEqualityComparer.GetStringComparer(comparer) is IEqualityComparer<string> stringComparer)
                {
                    comparer = (IEqualityComparer<TKey>)stringComparer;
                }
                else if (ReferenceEquals(comparer, EqualityComparer<TKey>.Default))
                {
                    _comparerIsDefaultForClasses = true;
                }
            }

            _tables = new Tables(buckets, locks, countPerLock, comparer);
            _growLockArray = growLockArray;
            _initialCapacity = capacity;
            _budget = buckets.Length / locks.Length;
        }

        /// <summary>
        /// Gets an instance of a type that may be used to perform operations on a <see cref="ConcurrentDictionary{TKey, TValue}"/>
        /// using a <typeparamref name="TAlternateKey"/> as a key instead of a <typeparamref name="TKey"/>.
        /// </summary>
        /// <typeparam name="TAlternateKey">The alternate type of a key for performing lookups.</typeparam>
        /// <returns>The created lookup instance.</returns>
        /// <exception cref="InvalidOperationException">This instance's comparer is not compatible with <typeparamref name="TAlternateKey"/>.</exception>
        /// <remarks>
        /// This instance must be using a comparer that implements <see cref="IAlternateEqualityComparer{TAlternateKey, TKey}"/> with
        /// <typeparamref name="TAlternateKey"/> and <typeparamref name="TKey"/>. If it doesn't, an exception will be thrown.
        /// </remarks>
        public AlternateLookup<TAlternateKey> GetAlternateLookup<TAlternateKey>() where TAlternateKey : notnull, allows ref struct
        {
            if (!IsCompatibleKey<TAlternateKey>(_tables))
            {
                ThrowHelper.ThrowInvalidOperationException(ExceptionResource.InvalidOperation_IncompatibleComparer);
            }

            return new AlternateLookup<TAlternateKey>(this);
        }

        /// <summary>
        /// Gets an instance of a type that may be used to perform operations on a <see cref="ConcurrentDictionary{TKey, TValue}"/>
        /// using a <typeparamref name="TAlternateKey"/> as a key instead of a <typeparamref name="TKey"/>.
        /// </summary>
        /// <typeparam name="TAlternateKey">The alternate type of a key for performing lookups.</typeparam>
        /// <param name="lookup">The created lookup instance when the method returns true, or a default instance that should not be used if the method returns false.</param>
        /// <returns>true if a lookup could be created; otherwise, false.</returns>
        /// <remarks>
        /// This instance must be using a comparer that implements <see cref="IAlternateEqualityComparer{TAlternateKey, TKey}"/> with
        /// <typeparamref name="TAlternateKey"/> and <typeparamref name="TKey"/>. If it doesn't, the method will return false.
        /// </remarks>
        public bool TryGetAlternateLookup<TAlternateKey>(out AlternateLookup<TAlternateKey> lookup) where TAlternateKey : notnull, allows ref struct
        {
            if (IsCompatibleKey<TAlternateKey>(_tables))
            {
                lookup = new AlternateLookup<TAlternateKey>(this);
                return true;
            }

            lookup = default;
            return false;
        }

        /// <summary>Computes the hash code for the specified key using the dictionary's comparer.</summary>
        /// <param name="comparer">
        /// The comparer. It's passed in to avoid having to look it up via a volatile read on <see cref="_tables"/>;
        /// such a comparer could also be incorrect if the table upgraded comparer concurrently.
        /// </param>
        /// <param name="key">The key for which to compute the hash code.</param>
        /// <returns>The hash code of the key.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int GetHashCode(IEqualityComparer<TKey>? comparer, TKey key)
        {
            if (typeof(TKey).IsValueType)
            {
                return comparer is null ? key.GetHashCode() : comparer.GetHashCode(key);
            }

            Debug.Assert(comparer is not null);
            return _comparerIsDefaultForClasses ? key.GetHashCode() : comparer.GetHashCode(key);
        }

        /// <summary>Determines whether the specified key and the key stored in the specified node are equal.</summary>
        /// <param name="comparer">
        /// The comparer. It's passed in to avoid having to look it up via a volatile read on <see cref="_tables"/>;
        /// such a comparer could also be incorrect if the table upgraded comparer concurrently.
        /// </param>
        /// <param name="node">The node containing the key to compare.</param>
        /// <param name="key">The other key to compare.</param>
        /// <returns>true if the keys are equal; otherwise, false.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool NodeEqualsKey(IEqualityComparer<TKey>? comparer, Node node, TKey key)
        {
            if (typeof(TKey).IsValueType)
            {
                return comparer is null ?
                    EqualityComparer<TKey>.Default.Equals(node._key, key) :
                    comparer.Equals(node._key, key);
            }

            Debug.Assert(comparer is not null);
            return comparer.Equals(node._key, key);
        }

        private void InitializeFromCollection(IEnumerable<KeyValuePair<TKey, TValue>> collection)
        {
            foreach (KeyValuePair<TKey, TValue> pair in collection)
            {
                if (pair.Key is null)
                {
                    ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
                }

                if (!TryAddInternal(_tables, pair.Key, null, pair.Value, updateIfExists: false, acquireLock: false, out _))
                {
                    throw new ArgumentException(SR.ConcurrentDictionary_SourceContainsDuplicateKeys);
                }
            }

            if (_budget == 0)
            {
                Tables tables = _tables;
                _budget = tables._buckets.Length / tables._locks.Length;
            }
        }

        /// <summary>
        /// Removes the specified key from the dictionary if it exists and returns its associated value.
        /// If matchValue flag is set, the key will be removed only if is associated with a particular
        /// value.
        /// </summary>
        /// <param name="key">The key to search for and remove if it exists.</param>
        /// <param name="value">The variable into which the removed value, if found, is stored.</param>
        /// <param name="matchValue">Whether removal of the key is conditional on its value.</param>
        /// <param name="oldValue">The conditional value to compare against if <paramref name="matchValue"/> is true</param>
        private bool TryRemoveInternal(TKey key, [MaybeNullWhen(false)] out TValue value, bool matchValue, TValue? oldValue)
        {
            Tables tables = _tables;

            IEqualityComparer<TKey>? comparer = tables._comparer;
            int hashcode = GetHashCode(comparer, key);

            while (true)
            {
                object[] locks = tables._locks;
                ref Node? bucket = ref GetBucketAndLock(tables, hashcode, out uint lockNo);

                lock (locks[lockNo])
                {
                    // If the table just got resized, we may not be holding the right lock, and must retry.
                    // This should be a rare occurrence.
                    if (tables != _tables)
                    {
                        tables = _tables;
                        if (!ReferenceEquals(comparer, tables._comparer))
                        {
                            comparer = tables._comparer;
                            hashcode = GetHashCode(comparer, key);
                        }
                        continue;
                    }

                    Node? prev = null;
                    for (Node? curr = bucket; curr is not null; curr = curr._next)
                    {
                        Debug.Assert((prev is null && curr == bucket) || prev!._next == curr);

                        if (hashcode == curr._hashcode && NodeEqualsKey(comparer, curr, key))
                        {
                            if (matchValue)
                            {
                                bool valuesMatch = EqualityComparer<TValue>.Default.Equals(oldValue, curr._value);
                                if (!valuesMatch)
                                {
                                    value = default;
                                    return false;
                                }
                            }

                            if (prev is null)
                            {
                                Volatile.Write(ref bucket, curr._next);
                            }
                            else
                            {
                                prev._next = curr._next;
                            }

                            value = curr._value;
                            tables._countPerLock[lockNo]--;
                            return true;
                        }
                        prev = curr;
                    }
                }

                value = default;
                return false;
            }
        }

        /// <summary>
        /// Attempts to get the value associated with the specified key from the <see cref="ConcurrentDictionary{TKey,TValue}"/>.
        /// </summary>
        /// <param name="key">The key of the value to get.</param>
        /// <param name="value">
        /// When this method returns, <paramref name="value"/> contains the object from
        /// the <see cref="ConcurrentDictionary{TKey,TValue}"/> with the specified key or the default value of
        /// <typeparamref name="TValue"/>, if the operation failed.
        /// </param>
        /// <returns>true if the key was found in the <see cref="ConcurrentDictionary{TKey,TValue}"/>; otherwise, false.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> is a null reference (Nothing in Visual Basic).</exception>
        public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value)
        {
            if (key is null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
            }

            Tables tables = _tables;

            IEqualityComparer<TKey>? comparer = tables._comparer;
            if (typeof(TKey).IsValueType && // comparer can only be null for value types; enable JIT to eliminate entire if block for ref types
                comparer is null)
            {
                int hashcode = key.GetHashCode();
                for (Node? n = GetBucket(tables, hashcode); n is not null; n = n._next)
                {
                    if (hashcode == n._hashcode && EqualityComparer<TKey>.Default.Equals(n._key, key))
                    {
                        value = n._value;
                        return true;
                    }
                }
            }
            else
            {
                Debug.Assert(comparer is not null);
                int hashcode = GetHashCode(comparer, key);
                for (Node? n = GetBucket(tables, hashcode); n is not null; n = n._next)
                {
                    if (hashcode == n._hashcode && comparer.Equals(n._key, key))
                    {
                        value = n._value;
                        return true;
                    }
                }
            }

            value = default;
            return false;
        }

        private static bool TryGetValueInternal(Tables tables, TKey key, int hashcode, [MaybeNullWhen(false)] out TValue value)
        {
            IEqualityComparer<TKey>? comparer = tables._comparer;

            if (typeof(TKey).IsValueType && // comparer can only be null for value types; enable JIT to eliminate entire if block for ref types
                comparer is null)
            {
                for (Node? n = GetBucket(tables, hashcode); n is not null; n = n._next)
                {
                    if (hashcode == n._hashcode && EqualityComparer<TKey>.Default.Equals(n._key, key))
                    {
                        value = n._value;
                        return true;
                    }
                }
            }
            else
            {
                Debug.Assert(comparer is not null);
                for (Node? n = GetBucket(tables, hashcode); n is not null; n = n._next)
                {
                    if (hashcode == n._hashcode && comparer.Equals(n._key, key))
                    {
                        value = n._value;
                        return true;
                    }
                }
            }

            value = default;
            return false;
        }

        /// <summary>
        /// Updates the value associated with <paramref name="key"/> to <paramref name="newValue"/> if the existing value is equal
        /// to <paramref name="comparisonValue"/>.
        /// </summary>
        /// <param name="key">The key whose value is compared with <paramref name="comparisonValue"/> and
        /// possibly replaced.</param>
        /// <param name="newValue">The value that replaces the value of the element with <paramref
        /// name="key"/> if the comparison results in equality.</param>
        /// <param name="comparisonValue">The value that is compared to the value of the element with
        /// <paramref name="key"/>.</param>
        /// <returns>
        /// true if the value with <paramref name="key"/> was equal to <paramref name="comparisonValue"/> and
        /// replaced with <paramref name="newValue"/>; otherwise, false.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> is a null reference.</exception>
        public bool TryUpdate(TKey key, TValue newValue, TValue comparisonValue)
        {
            if (key is null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
            }

            return TryUpdateInternal(_tables, key, null, newValue, comparisonValue);
        }

        /// <summary>
        /// Updates the value associated with <paramref name="key"/> to <paramref name="newValue"/> if the existing value is equal
        /// to <paramref name="comparisonValue"/>.
        /// </summary>
        /// <param name="tables">The tables that were used to create the hash code.</param>
        /// <param name="key">The key whose value is compared with <paramref name="comparisonValue"/> and
        /// possibly replaced.</param>
        /// <param name="nullableHashcode">The hashcode computed for <paramref name="key"/>.</param>
        /// <param name="newValue">The value that replaces the value of the element with <paramref
        /// name="key"/> if the comparison results in equality.</param>
        /// <param name="comparisonValue">The value that is compared to the value of the element with
        /// <paramref name="key"/>.</param>
        /// <returns>
        /// true if the value with <paramref name="key"/> was equal to <paramref name="comparisonValue"/> and
        /// replaced with <paramref name="newValue"/>; otherwise, false.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> is a null reference.</exception>
        private bool TryUpdateInternal(Tables tables, TKey key, int? nullableHashcode, TValue newValue, TValue comparisonValue)
        {
            IEqualityComparer<TKey>? comparer = tables._comparer;

            int hashcode = nullableHashcode ?? GetHashCode(comparer, key);
            Debug.Assert(nullableHashcode is null || nullableHashcode == hashcode);

            EqualityComparer<TValue> valueComparer = EqualityComparer<TValue>.Default;

            while (true)
            {
                object[] locks = tables._locks;
                ref Node? bucket = ref GetBucketAndLock(tables, hashcode, out uint lockNo);

                lock (locks[lockNo])
                {
                    // If the table just got resized, we may not be holding the right lock, and must retry.
                    // This should be a rare occurrence.
                    if (tables != _tables)
                    {
                        tables = _tables;
                        if (!ReferenceEquals(comparer, tables._comparer))
                        {
                            comparer = tables._comparer;
                            hashcode = GetHashCode(comparer, key);
                        }
                        continue;
                    }

                    // Try to find this key in the bucket
                    Node? prev = null;
                    for (Node? node = bucket; node is not null; node = node._next)
                    {
                        Debug.Assert((prev is null && node == bucket) || prev!._next == node);
                        if (hashcode == node._hashcode && NodeEqualsKey(comparer, node, key))
                        {
                            if (valueComparer.Equals(node._value, comparisonValue))
                            {
                                // Do the reference type check up front to handle many cases of shared generics.
                                // If TValue is a value type then the field's value here can be baked in. Otherwise,
                                // for the remaining shared generic cases the field access here would disqualify inlining,
                                // so the following check cannot be factored out of TryAddInternal/TryUpdateInternal.
                                if (!typeof(TValue).IsValueType || ConcurrentDictionaryTypeProps<TValue>.IsWriteAtomic)
                                {
                                    node._value = newValue;
                                }
                                else
                                {
                                    var newNode = new Node(node._key, newValue, hashcode, node._next);

                                    if (prev is null)
                                    {
                                        Volatile.Write(ref bucket, newNode);
                                    }
                                    else
                                    {
                                        prev._next = newNode;
                                    }
                                }

                                return true;
                            }

                            return false;
                        }

                        prev = node;
                    }

                    // didn't find the key
                    return false;
                }
            }
        }

        /// <summary>
        /// Removes all keys and values from the <see cref="ConcurrentDictionary{TKey,TValue}"/>.
        /// </summary>
        public void Clear()
        {
            int locksAcquired = 0;
            try
            {
                AcquireAllLocks(ref locksAcquired);

                // If the dictionary is already empty, then there's nothing to clear.
                if (AreAllBucketsEmpty())
                {
                    return;
                }

                Tables tables = _tables;
                var newTables = new Tables(new VolatileNode[HashHelpers.GetPrime(_initialCapacity)], tables._locks, new int[tables._countPerLock.Length], tables._comparer);
                _tables = newTables;
                _budget = Math.Max(1, newTables._buckets.Length / newTables._locks.Length);
            }
            finally
            {
                ReleaseLocks(locksAcquired);
            }
        }

        /// <summary>Copy dictionary contents to an array - shared implementation between ToArray and CopyTo.</summary>
        /// <remarks>Important: the caller must hold all locks in _locks before calling CopyToPairs.</remarks>
        private void CopyToPairs(KeyValuePair<TKey, TValue>[] array, int index)
        {
            foreach (VolatileNode bucket in _tables._buckets)
            {
                for (Node? current = bucket._node; current is not null; current = current._next)
                {
                    array[index] = new KeyValuePair<TKey, TValue>(current._key, current._value);
                    Debug.Assert(index < int.MaxValue, "This method should only be called when there's no overflow risk");
                    index++;
                }
            }
        }

        /// <summary>Copy dictionary contents to an array - shared implementation between ToArray and CopyTo.</summary>
        /// <remarks>Important: the caller must hold all locks in _locks before calling CopyToPairs.</remarks>
        private void CopyToEntries(DictionaryEntry[] array, int index)
        {
            foreach (VolatileNode bucket in _tables._buckets)
            {
                for (Node? current = bucket._node; current is not null; current = current._next)
                {
                    array[index] = new DictionaryEntry(current._key, current._value);
                    Debug.Assert(index < int.MaxValue, "This method should only be called when there's no overflow risk");
                    index++;
                }
            }
        }

        /// <summary>Copy dictionary contents to an array - shared implementation between ToArray and CopyTo.</summary>
        /// <remarks>Important: the caller must hold all locks in _locks before calling CopyToPairs.</remarks>
        private void CopyToObjects(object[] array, int index)
        {
            foreach (VolatileNode bucket in _tables._buckets)
            {
                for (Node? current = bucket._node; current is not null; current = current._next)
                {
                    array[index] = new KeyValuePair<TKey, TValue>(current._key, current._value);
                    Debug.Assert(index < int.MaxValue, "This method should only be called when there's no overflow risk");
                    index++;
                }
            }
        }

        /// <summary>Returns an enumerator that iterates through the <see
        /// cref="ConcurrentDictionary{TKey,TValue}"/>.</summary>
        /// <returns>An enumerator for the <see cref="ConcurrentDictionary{TKey,TValue}"/>.</returns>
        /// <remarks>
        /// The enumerator returned from the dictionary is safe to use concurrently with
        /// reads and writes to the dictionary, however it does not represent a moment-in-time snapshot
        /// of the dictionary.  The contents exposed through the enumerator may contain modifications
        /// made to the dictionary after <see cref="GetEnumerator"/> was called.
        /// </remarks>
        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => new Enumerator(this);

        /// <summary>Provides an enumerator implementation for the dictionary.</summary>
        private sealed class Enumerator(ConcurrentDictionary<TKey, TValue> dictionary) : IEnumerator<KeyValuePair<TKey, TValue>>
        {
            // Provides a manually-implemented version of (approximately) this iterator:
            //     VolatileNodeWrapper[] buckets = _tables._buckets;
            //     for (int i = 0; i < buckets.Length; i++)
            //         for (Node? current = buckets[i]._node; current is not null; current = current._next)
            //             yield return new KeyValuePair<TKey, TValue>(current._key, current._value);

            private readonly ConcurrentDictionary<TKey, TValue> _dictionary = dictionary;

            private ConcurrentDictionary<TKey, TValue>.VolatileNode[]? _buckets;
            private Node? _node;
            private int _i = -1;

            public KeyValuePair<TKey, TValue> Current { get; private set; }

            object IEnumerator.Current => Current;

            public void Reset()
            {
                _buckets = null;
                _node = null;
                Current = default;
                _i = -1;
            }

            public void Dispose() { }

            public bool MoveNext()
            {
                while (true)
                {
                    if (_node is Node node)
                    {
                        Current = new KeyValuePair<TKey, TValue>(node._key, node._value);
                        _node = node._next;
                        return true;
                    }

                    ConcurrentDictionary<TKey, TValue>.VolatileNode[]? buckets = _buckets ??= _dictionary._tables._buckets;
                    Debug.Assert(buckets is not null);

                    int i = _i + 1;
                    if ((uint)i >= (uint)buckets.Length)
                    {
                        return false;
                    }

                    _node = buckets[i]._node;
                    _i = i;
                }
            }
        }

        private bool TryAddInternal(TKey key, TValue value, bool updateIfExists, out TValue resultingValue) =>
            TryAddInternal(_tables, key, null, value, updateIfExists, acquireLock: true, out resultingValue);

        /// <summary>
        /// Shared internal implementation for inserts and updates.
        /// If key exists, we always return false; and if updateIfExists == true we force update with value;
        /// If key doesn't exist, we always add value and return true;
        /// </summary>
        private bool TryAddInternal(Tables tables, TKey key, int? nullableHashcode, TValue value, bool updateIfExists, bool acquireLock, out TValue resultingValue)
        {
            IEqualityComparer<TKey>? comparer = tables._comparer;

            int hashcode = nullableHashcode ?? GetHashCode(comparer, key);
            Debug.Assert(nullableHashcode is null || nullableHashcode == hashcode);

            while (true)
            {
                object[] locks = tables._locks;
                ref Node? bucket = ref GetBucketAndLock(tables, hashcode, out uint lockNo);

                bool resizeDesired = false;
                bool forceRehash = false;
                bool lockTaken = false;
                try
                {
                    if (acquireLock)
                    {
                        Monitor.Enter(locks[lockNo], ref lockTaken);
                    }

                    // If the table just got resized, we may not be holding the right lock, and must retry.
                    // This should be a rare occurrence.
                    if (tables != _tables)
                    {
                        tables = _tables;
                        if (!ReferenceEquals(comparer, tables._comparer))
                        {
                            comparer = tables._comparer;
                            hashcode = GetHashCode(comparer, key);
                        }
                        continue;
                    }

                    // Try to find this key in the bucket
                    uint collisionCount = 0;
                    Node? prev = null;
                    for (Node? node = bucket; node is not null; node = node._next)
                    {
                        Debug.Assert((prev is null && node == bucket) || prev!._next == node);
                        if (hashcode == node._hashcode && NodeEqualsKey(comparer, node, key))
                        {
                            // The key was found in the dictionary. If updates are allowed, update the value for that key.
                            // We need to create a new node for the update, in order to support TValue types that cannot
                            // be written atomically, since lock-free reads may be happening concurrently.
                            if (updateIfExists)
                            {
                                // Do the reference type check up front to handle many cases of shared generics.
                                // If TValue is a value type then the field's value here can be baked in. Otherwise,
                                // for the remaining shared generic cases the field access here would disqualify inlining,
                                // so the following check cannot be factored out of TryAddInternal/TryUpdateInternal.
                                if (!typeof(TValue).IsValueType || ConcurrentDictionaryTypeProps<TValue>.IsWriteAtomic)
                                {
                                    node._value = value;
                                }
                                else
                                {
                                    var newNode = new Node(node._key, value, hashcode, node._next);
                                    if (prev is null)
                                    {
                                        Volatile.Write(ref bucket, newNode);
                                    }
                                    else
                                    {
                                        prev._next = newNode;
                                    }
                                }
                                resultingValue = value;
                            }
                            else
                            {
                                resultingValue = node._value;
                            }
                            return false;
                        }
                        prev = node;
                        if (!typeof(TKey).IsValueType) // this is only relevant to strings, and we can avoid this code for all value types
                        {
                            collisionCount++;
                        }
                    }

                    // The key was not found in the bucket. Insert the key-value pair.
                    var resultNode = new Node(key, value, hashcode, bucket);
                    Volatile.Write(ref bucket, resultNode);
                    checked
                    {
                        tables._countPerLock[lockNo]++;
                    }

                    // If the number of elements guarded by this lock has exceeded the budget, resize the bucket table.
                    // It is also possible that GrowTable will increase the budget but won't resize the bucket table.
                    // That happens if the bucket table is found to be poorly utilized due to a bad hash function.
                    if (tables._countPerLock[lockNo] > _budget)
                    {
                        resizeDesired = true;
                    }

                    // We similarly want to invoke redo the tables if we're using a non-randomized comparer
                    // and need to upgrade to a randomized comparer due to too many collisions.
                    if (!typeof(TKey).IsValueType &&
                        collisionCount > HashHelpers.HashCollisionThreshold &&
                        comparer is NonRandomizedStringEqualityComparer)
                    {
                        forceRehash = true;
                    }
                }
                finally
                {
                    if (lockTaken)
                    {
                        Monitor.Exit(locks[lockNo]);
                    }
                }

                // The fact that we got here means that we just performed an insertion. If necessary, we will grow the table.
                //
                // Concurrency notes:
                // - Notice that we are not holding any locks at when calling GrowTable. This is necessary to prevent deadlocks.
                // - As a result, it is possible that GrowTable will be called unnecessarily. But, GrowTable will obtain lock 0
                //   and then verify that the table we passed to it as the argument is still the current table.
                if (resizeDesired | forceRehash)
                {
                    GrowTable(tables, resizeDesired, forceRehash);
                }

                resultingValue = value;
                return true;
            }
        }

        /// <summary>Gets or sets the value associated with the specified key.</summary>
        /// <param name="key">The key of the value to get or set.</param>
        /// <value>
        /// The value associated with the specified key. If the specified key is not found, a get operation throws a
        /// <see cref="KeyNotFoundException"/>, and a set operation creates a new element with the specified key.
        /// </value>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="key"/> is a null reference (Nothing in Visual Basic).
        /// </exception>
        /// <exception cref="KeyNotFoundException">
        /// The property is retrieved and <paramref name="key"/> does not exist in the collection.
        /// </exception>
        public TValue this[TKey key]
        {
            get
            {
                if (!TryGetValue(key, out TValue? value))
                {
                    ThrowKeyNotFoundException(key);
                }
                return value;
            }
            set
            {
                if (key is null)
                {
                    ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
                }

                TryAddInternal(_tables, key, null, value, updateIfExists: true, acquireLock: true, out _);
            }
        }

        /// <summary>
        /// Gets the <see cref="IEqualityComparer{TKey}" />
        /// that is used to determine equality of keys for the dictionary.
        /// </summary>
        /// <value>
        /// The <see cref="IEqualityComparer{TKey}" /> generic interface implementation
        /// that is used to determine equality of keys for the current
        /// <see cref="ConcurrentDictionary{TKey, TValue}" /> and to provide hash values for the keys.
        /// </value>
        /// <remarks>
        /// <see cref="ConcurrentDictionary{TKey, TValue}" /> requires an equality implementation to determine
        /// whether keys are equal. You can specify an implementation of the <see cref="IEqualityComparer{TKey}" />
        /// generic interface by using a constructor that accepts a comparer parameter;
        /// if you do not specify one, the default generic equality comparer <see cref="EqualityComparer{TKey}.Default" /> is used.
        /// </remarks>
        public IEqualityComparer<TKey> Comparer
        {
            get
            {
                IEqualityComparer<TKey>? comparer = _tables._comparer;

                if (typeof(TKey) == typeof(string))
                {
                    if ((comparer as NonRandomizedStringEqualityComparer)?.GetUnderlyingEqualityComparer() is IEqualityComparer<string> ec)
                    {
                        return (IEqualityComparer<TKey>)ec;
                    }
                }

                return comparer ?? EqualityComparer<TKey>.Default;
            }
        }

        /// <summary>Gets the number of pairs stored in the dictionary.</summary>
        /// <remarks>This assumes all of the dictionary's locks have been taken, or else the result may not be accurate.</remarks>
        private int GetCountNoLocks()
        {
            int count = 0;
            foreach (int value in _tables._countPerLock)
            {
                checked { count += value; }
            }

            return count;
        }

        /// <summary>
        /// Adds a key/value pair to the <see cref="ConcurrentDictionary{TKey,TValue}"/>
        /// if the key does not already exist.
        /// </summary>
        /// <param name="key">The key of the element to add.</param>
        /// <param name="valueFactory">The function used to generate a value for the key</param>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> is a null reference
        /// (Nothing in Visual Basic).</exception>
        /// <exception cref="ArgumentNullException"><paramref name="valueFactory"/> is a null reference
        /// (Nothing in Visual Basic).</exception>
        /// <exception cref="OverflowException">The dictionary contains too many
        /// elements.</exception>
        /// <returns>The value for the key.  This will be either the existing value for the key if the
        /// key is already in the dictionary, or the new value for the key as returned by valueFactory
        /// if the key was not in the dictionary.</returns>
        public TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
        {
            if (key is null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
            }

            if (valueFactory is null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.valueFactory);
            }

            Tables tables = _tables;

            IEqualityComparer<TKey>? comparer = tables._comparer;
            int hashcode = GetHashCode(comparer, key);

            if (!TryGetValueInternal(tables, key, hashcode, out TValue? resultingValue))
            {
                TryAddInternal(tables, key, hashcode, valueFactory(key), updateIfExists: false, acquireLock: true, out resultingValue);
            }

            return resultingValue;
        }

        /// <summary>
        /// Adds a key/value pair to the <see cref="ConcurrentDictionary{TKey,TValue}"/>
        /// if the key does not already exist.
        /// </summary>
        /// <param name="key">The key of the element to add.</param>
        /// <param name="valueFactory">The function used to generate a value for the key</param>
        /// <param name="factoryArgument">An argument value to pass into <paramref name="valueFactory"/>.</param>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> is a null reference
        /// (Nothing in Visual Basic).</exception>
        /// <exception cref="ArgumentNullException"><paramref name="valueFactory"/> is a null reference
        /// (Nothing in Visual Basic).</exception>
        /// <exception cref="OverflowException">The dictionary contains too many
        /// elements.</exception>
        /// <returns>The value for the key.  This will be either the existing value for the key if the
        /// key is already in the dictionary, or the new value for the key as returned by valueFactory
        /// if the key was not in the dictionary.</returns>
        public TValue GetOrAdd<TArg>(TKey key, Func<TKey, TArg, TValue> valueFactory, TArg factoryArgument)
            where TArg : allows ref struct
        {
            if (key is null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
            }

            if (valueFactory is null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.valueFactory);
            }

            Tables tables = _tables;

            IEqualityComparer<TKey>? comparer = tables._comparer;
            int hashcode = GetHashCode(comparer, key);

            if (!TryGetValueInternal(tables, key, hashcode, out TValue? resultingValue))
            {
                TryAddInternal(tables, key, hashcode, valueFactory(key, factoryArgument), updateIfExists: false, acquireLock: true, out resultingValue);
            }

            return resultingValue;
        }

        /// <summary>
        /// Adds a key/value pair to the <see cref="ConcurrentDictionary{TKey,TValue}"/>
        /// if the key does not already exist.
        /// </summary>
        /// <param name="key">The key of the element to add.</param>
        /// <param name="value">the value to be added, if the key does not already exist</param>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> is a null reference
        /// (Nothing in Visual Basic).</exception>
        /// <exception cref="OverflowException">The dictionary contains too many
        /// elements.</exception>
        /// <returns>The value for the key.  This will be either the existing value for the key if the
        /// key is already in the dictionary, or the new value if the key was not in the dictionary.</returns>
        public TValue GetOrAdd(TKey key, TValue value)
        {
            if (key is null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
            }

            Tables tables = _tables;

            IEqualityComparer<TKey>? comparer = tables._comparer;
            int hashcode = GetHashCode(comparer, key);

            if (!TryGetValueInternal(tables, key, hashcode, out TValue? resultingValue))
            {
                TryAddInternal(tables, key, hashcode, value, updateIfExists: false, acquireLock: true, out resultingValue);
            }

            return resultingValue;
        }

        /// <summary>
        /// Adds a key/value pair to the <see cref="ConcurrentDictionary{TKey,TValue}"/> if the key does not already
        /// exist, or updates a key/value pair in the <see cref="ConcurrentDictionary{TKey,TValue}"/> if the key
        /// already exists.
        /// </summary>
        /// <param name="key">The key to be added or whose value should be updated</param>
        /// <param name="addValueFactory">The function used to generate a value for an absent key</param>
        /// <param name="updateValueFactory">The function used to generate a new value for an existing key
        /// based on the key's existing value</param>
        /// <param name="factoryArgument">An argument to pass into <paramref name="addValueFactory"/> and <paramref name="updateValueFactory"/>.</param>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> is a null reference
        /// (Nothing in Visual Basic).</exception>
        /// <exception cref="ArgumentNullException"><paramref name="addValueFactory"/> is a null reference
        /// (Nothing in Visual Basic).</exception>
        /// <exception cref="ArgumentNullException"><paramref name="updateValueFactory"/> is a null reference
        /// (Nothing in Visual Basic).</exception>
        /// <exception cref="OverflowException">The dictionary contains too many
        /// elements.</exception>
        /// <returns>The new value for the key.  This will be either be the result of addValueFactory (if the key was
        /// absent) or the result of updateValueFactory (if the key was present).</returns>
        public TValue AddOrUpdate<TArg>(
            TKey key, Func<TKey, TArg, TValue> addValueFactory, Func<TKey, TValue, TArg, TValue> updateValueFactory, TArg factoryArgument)
            where TArg : allows ref struct
        {
            if (key is null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
            }

            if (addValueFactory is null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.addValueFactory);
            }

            if (updateValueFactory is null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.updateValueFactory);
            }

            Tables tables = _tables;

            IEqualityComparer<TKey>? comparer = tables._comparer;
            int hashcode = GetHashCode(comparer, key);

            while (true)
            {
                if (TryGetValueInternal(tables, key, hashcode, out TValue? oldValue))
                {
                    // key exists, try to update
                    TValue newValue = updateValueFactory(key, oldValue, factoryArgument);
                    if (TryUpdateInternal(tables, key, hashcode, newValue, oldValue))
                    {
                        return newValue;
                    }
                }
                else
                {
                    // key doesn't exist, try to add
                    if (TryAddInternal(tables, key, hashcode, addValueFactory(key, factoryArgument), updateIfExists: false, acquireLock: true, out TValue resultingValue))
                    {
                        return resultingValue;
                    }
                }

                if (tables != _tables)
                {
                    tables = _tables;
                    if (!ReferenceEquals(comparer, tables._comparer))
                    {
                        comparer = tables._comparer;
                        hashcode = GetHashCode(comparer, key);
                    }
                }
            }
        }

        /// <summary>
        /// Adds a key/value pair to the <see cref="ConcurrentDictionary{TKey,TValue}"/> if the key does not already
        /// exist, or updates a key/value pair in the <see cref="ConcurrentDictionary{TKey,TValue}"/> if the key
        /// already exists.
        /// </summary>
        /// <param name="key">The key to be added or whose value should be updated</param>
        /// <param name="addValueFactory">The function used to generate a value for an absent key</param>
        /// <param name="updateValueFactory">The function used to generate a new value for an existing key
        /// based on the key's existing value</param>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> is a null reference
        /// (Nothing in Visual Basic).</exception>
        /// <exception cref="ArgumentNullException"><paramref name="addValueFactory"/> is a null reference
        /// (Nothing in Visual Basic).</exception>
        /// <exception cref="ArgumentNullException"><paramref name="updateValueFactory"/> is a null reference
        /// (Nothing in Visual Basic).</exception>
        /// <exception cref="OverflowException">The dictionary contains too many
        /// elements.</exception>
        /// <returns>The new value for the key.  This will be either the result of addValueFactory (if the key was
        /// absent) or the result of updateValueFactory (if the key was present).</returns>
        public TValue AddOrUpdate(TKey key, Func<TKey, TValue> addValueFactory, Func<TKey, TValue, TValue> updateValueFactory)
        {
            if (key is null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
            }

            if (addValueFactory is null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.addValueFactory);
            }

            if (updateValueFactory is null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.updateValueFactory);
            }

            Tables tables = _tables;

            IEqualityComparer<TKey>? comparer = tables._comparer;
            int hashcode = GetHashCode(comparer, key);

            while (true)
            {
                if (TryGetValueInternal(tables, key, hashcode, out TValue? oldValue))
                {
                    // key exists, try to update
                    TValue newValue = updateValueFactory(key, oldValue);
                    if (TryUpdateInternal(tables, key, hashcode, newValue, oldValue))
                    {
                        return newValue;
                    }
                }
                else
                {
                    // key doesn't exist, try to add
                    if (TryAddInternal(tables, key, hashcode, addValueFactory(key), updateIfExists: false, acquireLock: true, out TValue resultingValue))
                    {
                        return resultingValue;
                    }
                }

                if (tables != _tables)
                {
                    tables = _tables;
                    if (!ReferenceEquals(comparer, tables._comparer))
                    {
                        comparer = tables._comparer;
                        hashcode = GetHashCode(comparer, key);
                    }
                }
            }
        }

        /// <summary>
        /// Adds a key/value pair to the <see cref="ConcurrentDictionary{TKey,TValue}"/> if the key does not already
        /// exist, or updates a key/value pair in the <see cref="ConcurrentDictionary{TKey,TValue}"/> if the key
        /// already exists.
        /// </summary>
        /// <param name="key">The key to be added or whose value should be updated</param>
        /// <param name="addValue">The value to be added for an absent key</param>
        /// <param name="updateValueFactory">The function used to generate a new value for an existing key based on
        /// the key's existing value</param>
        /// <exception cref="ArgumentNullException"><paramref name="key"/> is a null reference
        /// (Nothing in Visual Basic).</exception>
        /// <exception cref="ArgumentNullException"><paramref name="updateValueFactory"/> is a null reference
        /// (Nothing in Visual Basic).</exception>
        /// <exception cref="OverflowException">The dictionary contains too many
        /// elements.</exception>
        /// <returns>The new value for the key.  This will be either the value of addValue (if the key was
        /// absent) or the result of updateValueFactory (if the key was present).</returns>
        public TValue AddOrUpdate(TKey key, TValue addValue, Func<TKey, TValue, TValue> updateValueFactory)
        {
            if (key is null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
            }

            if (updateValueFactory is null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.updateValueFactory);
            }

            Tables tables = _tables;

            IEqualityComparer<TKey>? comparer = tables._comparer;
            int hashcode = GetHashCode(comparer, key);

            while (true)
            {
                if (TryGetValueInternal(tables, key, hashcode, out TValue? oldValue))
                {
                    // key exists, try to update
                    TValue newValue = updateValueFactory(key, oldValue);
                    if (TryUpdateInternal(tables, key, hashcode, newValue, oldValue))
                    {
                        return newValue;
                    }
                }
                else
                {
                    // key doesn't exist, try to add
                    if (TryAddInternal(tables, key, hashcode, addValue, updateIfExists: false, acquireLock: true, out TValue resultingValue))
                    {
                        return resultingValue;
                    }
                }

                if (tables != _tables)
                {
                    tables = _tables;
                    if (!ReferenceEquals(comparer, tables._comparer))
                    {
                        comparer = tables._comparer;
                        hashcode = GetHashCode(comparer, key);
                    }
                }
            }
        }

        /// <summary>
        /// Gets a value that indicates whether the <see cref="ConcurrentDictionary{TKey,TValue}"/> is empty.
        /// </summary>
        /// <value>true if the <see cref="ConcurrentDictionary{TKey,TValue}"/> is empty; otherwise,
        /// false.</value>
        public bool IsEmpty
        {
            get
            {
                // Check if any buckets are non-empty, without acquiring any locks.
                // This fast path should generally suffice as collections are usually not empty.
                if (!AreAllBucketsEmpty())
                {
                    return false;
                }

                // We didn't see any buckets containing items, however we can't be sure
                // the collection was actually empty at any point in time as items may have been
                // added and removed while iterating over the buckets such that we never saw an
                // empty bucket, but there was always an item present in at least one bucket.
                int locksAcquired = 0;
                try
                {
                    AcquireAllLocks(ref locksAcquired);

                    return AreAllBucketsEmpty();
                }
                finally
                {
                    ReleaseLocks(locksAcquired);
                }
            }
        }

        private bool AreAllBucketsEmpty() =>
            !_tables._countPerLock.AsSpan().ContainsAnyExcept(0);

        /// <summary>
        /// Replaces the bucket table with a larger one. To prevent multiple threads from resizing the
        /// table as a result of races, the Tables instance that holds the table of buckets deemed too
        /// small is passed in as an argument to GrowTable(). GrowTable() obtains a lock, and then checks
        /// the Tables instance has been replaced in the meantime or not.
        /// </summary>
        private void GrowTable(Tables tables, bool resizeDesired, bool forceRehashIfNonRandomized)
        {
            int locksAcquired = 0;
            try
            {
                // The thread that first obtains _locks[0] will be the one doing the resize operation
                AcquireFirstLock(ref locksAcquired);

                // Make sure nobody resized the table while we were waiting for lock 0:
                if (tables != _tables)
                {
                    // We assume that since the table reference is different, it was already resized (or the budget
                    // was adjusted). If we ever decide to do table shrinking, or replace the table for other reasons,
                    // we will have to revisit this logic.
                    return;
                }

                int newLength = tables._buckets.Length;

                IEqualityComparer<TKey>? upgradeComparer = null;
                if (forceRehashIfNonRandomized && tables._comparer is NonRandomizedStringEqualityComparer nrsec)
                {
                    upgradeComparer = (IEqualityComparer<TKey>)nrsec.GetUnderlyingEqualityComparer();
                }

                if (resizeDesired)
                {
                    // Compute the (approx.) total size. Use an Int64 accumulation variable to avoid an overflow.
                    // If the bucket array is too empty, we have an imbalance.
                    // If we have a string key and we're still using a non-randomized comparer,
                    // take this as a sign that we need to upgrade to one.
                    // Otherwise, double the budget instead of resizing the table.
                    if (upgradeComparer is null && GetCountNoLocks() < tables._buckets.Length / 4)
                    {
                        _budget = 2 * _budget;
                        if (_budget < 0)
                        {
                            _budget = int.MaxValue;
                        }
                        return;
                    }

                    // Compute the new table size at least twice the previous table size.
                    // Double the size of the buckets table and choose a prime that's at least as large.
                    if ((newLength = tables._buckets.Length * 2) < 0 ||
                        (newLength = HashHelpers.GetPrime(newLength)) > Array.MaxLength)
                    {
                        newLength = Array.MaxLength;

                        // We want to make sure that GrowTable will not be called again, since table is at the maximum size.
                        // To achieve that, we set the budget to int.MaxValue.
                        //
                        // (There is one special case that would allow GrowTable() to be called in the future:
                        // calling Clear() on the ConcurrentDictionary will shrink the table and lower the budget.)
                        _budget = int.MaxValue;
                    }
                }

                object[] newLocks = tables._locks;

                // Add more locks
                if (_growLockArray && tables._locks.Length < MaxLockNumber)
                {
                    newLocks = new object[tables._locks.Length * 2];
                    Array.Copy(tables._locks, newLocks, tables._locks.Length);
                    for (int i = tables._locks.Length; i < newLocks.Length; i++)
                    {
                        newLocks[i] = new object();
                    }
                }

                var newBuckets = new VolatileNode[newLength];
                var newCountPerLock = new int[newLocks.Length];
                var newTables = new Tables(newBuckets, newLocks, newCountPerLock, upgradeComparer ?? tables._comparer);

                // Now acquire all other locks for the table
                AcquirePostFirstLock(tables, ref locksAcquired);

                // Copy all data into a new table, creating new nodes for all elements
                foreach (VolatileNode bucket in tables._buckets)
                {
                    Node? current = bucket._node;
                    while (current is not null)
                    {
                        int hashCode = upgradeComparer is null ? current._hashcode : upgradeComparer.GetHashCode(current._key);

                        Node? next = current._next;
                        ref Node? newBucket = ref GetBucketAndLock(newTables, hashCode, out uint newLockNo);

                        newBucket = new Node(current._key, current._value, hashCode, newBucket);

                        checked
                        {
                            newCountPerLock[newLockNo]++;
                        }

                        current = next;
                    }
                }

                // Adjust the budget
                _budget = Math.Max(1, newBuckets.Length / newLocks.Length);

                // Replace tables with the new versions
                _tables = newTables;
            }
            finally
            {
                ReleaseLocks(locksAcquired);
            }
        }

        /// <summary>
        /// Acquires all locks for this hash table, and increments locksAcquired by the number
        /// of locks that were successfully acquired. The locks are acquired in an increasing
        /// order.
        /// </summary>
        private void AcquireAllLocks(ref int locksAcquired)
        {
            // First, acquire lock 0, then acquire the rest. _tables won't change after acquiring lock 0.
            AcquireFirstLock(ref locksAcquired);
            AcquirePostFirstLock(_tables, ref locksAcquired);
            Debug.Assert(locksAcquired == _tables._locks.Length);
        }

        /// <summary>Acquires the first lock.</summary>
        /// <param name="locksAcquired">The number of locks acquired. It should be 0 on entry and 1 on exit.</param>
        /// <remarks>
        /// Once the caller owns the lock on lock 0, _tables._locks will not change (i.e., grow),
        /// so a caller can safely snap _tables._locks to read the remaining locks. When the locks array grows,
        /// even though the array object itself changes, the locks from the previous array are kept.
        /// </remarks>
        private void AcquireFirstLock(ref int locksAcquired)
        {
            object[] locks = _tables._locks;
            Debug.Assert(locksAcquired == 0);
            Debug.Assert(!Monitor.IsEntered(locks[0]));

            Monitor.Enter(locks[0]);
            locksAcquired = 1;
        }

        /// <summary>Acquires all of the locks after the first, which must already be acquired.</summary>
        /// <param name="tables">The tables snapped after the first lock was acquired.</param>
        /// <param name="locksAcquired">
        /// The number of locks acquired, which should be 1 on entry.  It's incremented as locks
        /// are taken so that the caller can reliably release those locks in a finally in case
        /// of exception.
        /// </param>
        private static void AcquirePostFirstLock(Tables tables, ref int locksAcquired)
        {
            object[] locks = tables._locks;
            Debug.Assert(Monitor.IsEntered(locks[0]));
            Debug.Assert(locksAcquired == 1);

            for (int i = 1; i < locks.Length; i++)
            {
                Monitor.Enter(locks[i]);
                locksAcquired++;
            }

            Debug.Assert(locksAcquired == locks.Length);
        }

        /// <summary>Releases all of the locks up to the specified number acquired.</summary>
        /// <param name="locksAcquired">The number of locks acquired.  All lock numbers in the range [0, locksAcquired) will be released.</param>
        private void ReleaseLocks(int locksAcquired)
        {
            Debug.Assert(locksAcquired >= 0);

            object[] locks = _tables._locks;
            for (int i = 0; i < locksAcquired; i++)
            {
                Monitor.Exit(locks[i]);
            }
        }

        /// <summary>
        /// Gets a collection containing the keys in the dictionary.
        /// </summary>
        private ReadOnlyCollection<TKey> GetKeys()
        {
            int locksAcquired = 0;
            try
            {
                AcquireAllLocks(ref locksAcquired);

                int count = GetCountNoLocks();
                if (count == 0)
                {
                    return ReadOnlyCollection<TKey>.Empty;
                }

                var keys = new TKey[count];
                int i = 0;
                foreach (VolatileNode bucket in _tables._buckets)
                {
                    for (Node? node = bucket._node; node is not null; node = node._next)
                    {
                        keys[i] = node._key;
                        i++;
                    }
                }
                Debug.Assert(i == count);

                return new ReadOnlyCollection<TKey>(keys);
            }
            finally
            {
                ReleaseLocks(locksAcquired);
            }
        }

        /// <summary>
        /// Gets a collection containing the values in the dictionary.
        /// </summary>
        private ReadOnlyCollection<TValue> GetValues()
        {
            int locksAcquired = 0;
            try
            {
                AcquireAllLocks(ref locksAcquired);

                int count = GetCountNoLocks();
                if (count == 0)
                {
                    return ReadOnlyCollection<TValue>.Empty;
                }

                var keys = new TValue[count];
                int i = 0;
                foreach (VolatileNode bucket in _tables._buckets)
                {
                    for (Node? node = bucket._node; node is not null; node = node._next)
                    {
                        keys[i] = node._value;
                        i++;
                    }
                }
                Debug.Assert(i == count);

                return new ReadOnlyCollection<TValue>(keys);
            }
            finally
            {
                ReleaseLocks(locksAcquired);
            }
        }

        private struct VolatileNode
        {
            // Workaround for https://github.com/dotnet/runtime/issues/65789.
            // If we had a Node?[] array, to safely read from the array we'd need to do Volatile.Read(ref array[i]),
            // but that triggers an unnecessary ldelema, which in turn results in a call to CastHelpers.LdelemaRef.
            // With this wrapper, the non-inlined call disappears.
            internal volatile Node? _node;
        }

        /// <summary>
        /// A node in a singly-linked list representing a particular hash table bucket.
        /// </summary>
        private sealed class Node
        {
            internal readonly TKey _key;
            internal TValue _value;
            internal volatile Node? _next;
            internal readonly int _hashcode;

            internal Node(TKey key, TValue value, int hashcode, Node? next)
            {
                _key = key;
                _value = value;
                _next = next;
                _hashcode = hashcode;
            }
        }

        /// <summary>Computes a ref to the bucket for a particular key.</summary>
        /// <remarks>This reads the bucket with a read acquire barrier.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Node? GetBucket(Tables tables, int hashcode)
        {
            VolatileNode[] buckets = tables._buckets;
            if (IntPtr.Size == 8)
            {
                return buckets[HashHelpers.FastMod((uint)hashcode, (uint)buckets.Length, tables._fastModBucketsMultiplier)]._node;
            }
            else
            {
                return buckets[(uint)hashcode % (uint)buckets.Length]._node;
            }
        }

        /// <summary>Computes the bucket and lock number for a particular key.</summary>
        /// <remarks>This returns a ref to the bucket node; no barriers are employed.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ref Node? GetBucketAndLock(Tables tables, int hashcode, out uint lockNo)
        {
            VolatileNode[] buckets = tables._buckets;
            uint bucketNo;
            if (IntPtr.Size == 8)
            {
                bucketNo = HashHelpers.FastMod((uint)hashcode, (uint)buckets.Length, tables._fastModBucketsMultiplier);
            }
            else
            {
                bucketNo = (uint)hashcode % (uint)buckets.Length;
            }
            lockNo = bucketNo % (uint)tables._locks.Length; // doesn't use FastMod, as it would require maintaining a different multiplier
            return ref buckets[bucketNo]._node;
        }

        /// <summary>Tables that hold the internal state of the ConcurrentDictionary</summary>
        /// <remarks>Wrapping all of the mutable state into a single object allows us to swap in everything atomically.</remarks>
        private sealed class Tables
        {
            /// <summary>The comparer to use for lookups in the tables.</summary>
            internal readonly IEqualityComparer<TKey>? _comparer;
            /// <summary>A singly-linked list for each bucket.</summary>
            internal readonly VolatileNode[] _buckets;
            /// <summary>Pre-computed multiplier for use on 64-bit performing faster modulo operations.</summary>
            internal readonly ulong _fastModBucketsMultiplier;
            /// <summary>A set of locks, each guarding a section of the table.</summary>
            internal readonly object[] _locks;
            /// <summary>The number of elements guarded by each lock.</summary>
            internal readonly int[] _countPerLock;

            internal Tables(VolatileNode[] buckets, object[] locks, int[] countPerLock, IEqualityComparer<TKey>? comparer)
            {
                Debug.Assert(typeof(TKey).IsValueType || comparer is not null);

                _buckets = buckets;
                _locks = locks;
                _countPerLock = countPerLock;
                _comparer = comparer;
                if (IntPtr.Size == 8)
                {
                    _fastModBucketsMultiplier = HashHelpers.GetFastModMultiplier((uint)buckets.Length);
                }
            }
        }

        /// <summary>Checks whether the dictionary has a comparer compatible with <typeparamref name="TAlternateKey"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsCompatibleKey<TAlternateKey>(ConcurrentDictionary<TKey, TValue>.Tables tables)
            where TAlternateKey : notnull, allows ref struct
        {
            Debug.Assert(tables is not null);
            return tables._comparer is IAlternateEqualityComparer<TAlternateKey, TKey>;
        }

        /// <summary>Gets the dictionary's alternate comparer. The dictionary must have already been verified as compatible.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static IAlternateEqualityComparer<TAlternateKey, TKey> GetAlternateComparer<TAlternateKey>(ConcurrentDictionary<TKey, TValue>.Tables tables)
             where TAlternateKey : notnull, allows ref struct
        {
            Debug.Assert(IsCompatibleKey<TAlternateKey>(tables));
            return Unsafe.As<IAlternateEqualityComparer<TAlternateKey, TKey>>(tables._comparer!);
        }

        public readonly partial struct AlternateLookup<TAlternateKey> where TAlternateKey : notnull, allows ref struct
        {
            /// <summary>Initialize the instance. The dictionary must have already been verified to have a compatible comparer.</summary>
            internal AlternateLookup(ConcurrentDictionary<TKey, TValue> dictionary)
            {
                Debug.Assert(dictionary is not null);
                Debug.Assert(IsCompatibleKey<TAlternateKey>(dictionary._tables));
                Dictionary = dictionary;
            }

            /// <summary>Attempts to add the specified key and value to the dictionary.</summary>
            /// <param name="key">The alternate key of the element to add.</param>
            /// <param name="value">The value of the element to add.</param>
            /// <param name="updateIfExists">true to overwrite the value of an existing key; false to throw for an existing key.</param>
            /// <param name="resultingValue">The value associated with the key when the operation completes.</param>
            private bool TryAdd(TAlternateKey key, TValue value, bool updateIfExists, out TValue resultingValue)
            {
                if (key is null)
                {
                    ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
                }

                Tables tables = Dictionary._tables;
                IAlternateEqualityComparer<TAlternateKey, TKey> comparer = GetAlternateComparer<TAlternateKey>(tables);

                int hashcode = comparer.GetHashCode(key);

                while (true)
                {
                    object[] locks = tables._locks;
                    ref Node? bucket = ref GetBucketAndLock(tables, hashcode, out uint lockNo);

                    bool resizeDesired = false;
                    bool forceRehash = false;
                    bool lockTaken = false;
                    try
                    {
                        Monitor.Enter(locks[lockNo], ref lockTaken);

                        // If the table just got resized, we may not be holding the right lock, and must retry.
                        // This should be a rare occurrence.
                        if (tables != Dictionary._tables)
                        {
                            tables = Dictionary._tables;
                            if (!ReferenceEquals(comparer, tables._comparer))
                            {
                                comparer = GetAlternateComparer<TAlternateKey>(tables);
                                hashcode = comparer.GetHashCode(key);
                            }
                            continue;
                        }

                        // Try to find this key in the bucket
                        uint collisionCount = 0;
                        Node? prev = null;
                        for (Node? node = bucket; node is not null; node = node._next)
                        {
                            Debug.Assert((prev is null && node == bucket) || prev!._next == node);
                            if (hashcode == node._hashcode && comparer.Equals(key, node._key))
                            {
                                // The key was found in the dictionary. If updates are allowed, update the value for that key.
                                // We need to create a new node for the update, in order to support TValue types that cannot
                                // be written atomically, since lock-free reads may be happening concurrently.
                                if (updateIfExists)
                                {
                                    // Do the reference type check up front to handle many cases of shared generics.
                                    // If TValue is a value type then the field's value here can be baked in. Otherwise,
                                    // for the remaining shared generic cases the field access here would disqualify inlining,
                                    // so the following check cannot be factored out of TryAddInternal/TryUpdateInternal.
                                    if (!typeof(TValue).IsValueType || ConcurrentDictionaryTypeProps<TValue>.IsWriteAtomic)
                                    {
                                        node._value = value;
                                    }
                                    else
                                    {
                                        var newNode = new Node(node._key, value, hashcode, node._next);
                                        if (prev is null)
                                        {
                                            Volatile.Write(ref bucket, newNode);
                                        }
                                        else
                                        {
                                            prev._next = newNode;
                                        }
                                    }
                                    resultingValue = value;
                                }
                                else
                                {
                                    resultingValue = node._value;
                                }
                                return false;
                            }
                            prev = node;
                            if (!typeof(TKey).IsValueType) // this is only relevant to strings, and we can avoid this code for all value types
                            {
                                collisionCount++;
                            }
                        }

                        TKey actualKey = comparer.Create(key);
                        if (actualKey is null)
                        {
                            ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
                        }

                        // The key was not found in the bucket. Insert the key-value pair.
                        var resultNode = new Node(actualKey, value, hashcode, bucket);
                        Volatile.Write(ref bucket, resultNode);
                        checked
                        {
                            tables._countPerLock[lockNo]++;
                        }

                        // If the number of elements guarded by this lock has exceeded the budget, resize the bucket table.
                        // It is also possible that GrowTable will increase the budget but won't resize the bucket table.
                        // That happens if the bucket table is found to be poorly utilized due to a bad hash function.
                        if (tables._countPerLock[lockNo] > Dictionary._budget)
                        {
                            resizeDesired = true;
                        }

                        // We similarly want to invoke redo the tables if we're using a non-randomized comparer
                        // and need to upgrade to a randomized comparer due to too many collisions.
                        if (!typeof(TKey).IsValueType &&
                            collisionCount > HashHelpers.HashCollisionThreshold &&
                            comparer is NonRandomizedStringEqualityComparer)
                        {
                            forceRehash = true;
                        }
                    }
                    finally
                    {
                        if (lockTaken)
                        {
                            Monitor.Exit(locks[lockNo]);
                        }
                    }

                    // The fact that we got here means that we just performed an insertion. If necessary, we will grow the table.
                    // See comments in TryAddInternal.
                    if (resizeDesired | forceRehash)
                    {
                        Dictionary.GrowTable(tables, resizeDesired, forceRehash);
                    }

                    resultingValue = value;
                    return true;
                }
            }

            /// <summary>Gets the value associated with the specified alternate key.</summary>
            /// <param name="key">The alternate key of the value to get.</param>
            /// <param name="actualKey">
            /// When this method returns, contains the actual key associated with the alternate key, if the key is found;
            /// otherwise, the default value for the type of the key parameter.
            /// </param>
            /// <param name="value">
            /// When this method returns, contains the value associated with the specified key, if the key is found;
            /// otherwise, the default value for the type of the value parameter.
            /// </param>
            /// <returns><see langword="true"/> if an entry was found; otherwise, <see langword="false"/>.</returns>
            /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
            public bool TryGetValue(TAlternateKey key, [MaybeNullWhen(false)] out TKey actualKey, [MaybeNullWhen(false)] out TValue value)
            {
                if (key is null)
                {
                    ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
                }

                Tables tables = Dictionary._tables;
                IAlternateEqualityComparer<TAlternateKey, TKey> comparer = GetAlternateComparer<TAlternateKey>(tables);

                int hashcode = comparer.GetHashCode(key);
                for (Node? n = GetBucket(tables, hashcode); n is not null; n = n._next)
                {
                    if (hashcode == n._hashcode && comparer.Equals(key, n._key))
                    {
                        actualKey = n._key;
                        value = n._value;
                        return true;
                    }
                }

                actualKey = default;
                value = default;
                return false;
            }

            /// <summary>
            /// Removes the value with the specified alternate key from the <see cref="Dictionary{TKey, TValue}"/>,
            /// and copies the associated key and element to the value parameter.
            /// </summary>
            /// <param name="key">The alternate key of the element to remove.</param>
            /// <param name="actualKey">The removed key.</param>
            /// <param name="value">The removed element.</param>
            /// <returns>true if the element is successfully found and removed; otherwise, false.</returns>
            /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
            public bool TryRemove(TAlternateKey key, [MaybeNullWhen(false)] out TKey actualKey, [MaybeNullWhen(false)] out TValue value)
            {
                if (key is null)
                {
                    ThrowHelper.ThrowArgumentNullException(ExceptionArgument.key);
                }

                Tables tables = Dictionary._tables;
                IAlternateEqualityComparer<TAlternateKey, TKey> comparer = GetAlternateComparer<TAlternateKey>(tables);
                int hashcode = comparer.GetHashCode(key);

                while (true)
                {
                    object[] locks = tables._locks;
                    ref Node? bucket = ref GetBucketAndLock(tables, hashcode, out uint lockNo);

                    // Do a hot read on number of items stored in the bucket.  If it's empty, we can avoid
                    // taking the lock and fail fast.
                    if (tables._countPerLock[lockNo] != 0)
                    {
                        lock (locks[lockNo])
                        {
                            // If the table just got resized, we may not be holding the right lock, and must retry.
                            // This should be a rare occurrence.
                            if (tables != Dictionary._tables)
                            {
                                tables = Dictionary._tables;
                                if (!ReferenceEquals(comparer, tables._comparer))
                                {
                                    comparer = GetAlternateComparer<TAlternateKey>(tables);
                                    hashcode = comparer.GetHashCode(key);
                                }
                                continue;
                            }

                            Node? prev = null;
                            for (Node? curr = bucket; curr is not null; curr = curr._next)
                            {
                                Debug.Assert((prev is null && curr == bucket) || prev!._next == curr);

                                if (hashcode == curr._hashcode && comparer.Equals(key, curr._key))
                                {
                                    if (prev is null)
                                    {
                                        Volatile.Write(ref bucket, curr._next);
                                    }
                                    else
                                    {
                                        prev._next = curr._next;
                                    }

                                    actualKey = curr._key;
                                    value = curr._value;
                                    tables._countPerLock[lockNo]--;
                                    return true;
                                }
                                prev = curr;
                            }
                        }
                    }

                    actualKey = default;
                    value = default;
                    return false;
                }
            }
        }
    }

    internal static class ConcurrentDictionaryTypeProps<T>
    {
        /// <summary>Whether T's type can be written atomically (i.e., with no danger of torn reads).</summary>
        internal static readonly bool IsWriteAtomic = IsWriteAtomicPrivate();

        private static bool IsWriteAtomicPrivate()
        {
            // Section 12.6.6 of ECMA CLI explains which types can be read and written atomically without
            // the risk of tearing. See https://www.ecma-international.org/publications/files/ECMA-ST/ECMA-335.pdf

            if (!typeof(T).IsValueType ||
                typeof(T) == typeof(IntPtr) ||
                typeof(T) == typeof(UIntPtr))
            {
                return true;
            }

            switch (Type.GetTypeCode(typeof(T)))
            {
                case TypeCode.Boolean:
                case TypeCode.Byte:
                case TypeCode.Char:
                case TypeCode.Int16:
                case TypeCode.Int32:
                case TypeCode.SByte:
                case TypeCode.Single:
                case TypeCode.UInt16:
                case TypeCode.UInt32:
                    return true;

                case TypeCode.Double:
                case TypeCode.Int64:
                case TypeCode.UInt64:
                    return IntPtr.Size == 8;

                default:
                    return false;
            }
        }
    }
}
