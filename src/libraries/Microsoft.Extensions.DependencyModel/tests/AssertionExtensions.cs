// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Microsoft.Extensions.DependencyModel.Tests
{
    public static class AssertionExtensions
    {
        public static JsonAssertions Should(this JToken jToken)
        {
            return new JsonAssertions(jToken);
        }

        public static CompilationLibraryArrayAssertions Should(this CompilationLibrary[] libraries)
        {
            return new CompilationLibraryArrayAssertions(libraries);
        }

        public static EnumerableAssertions<T> Should<T>(this IEnumerable<T> enumerable)
        {
            return new EnumerableAssertions<T>(enumerable);
        }

        public static StringEnumerableAssertions Should(this IEnumerable<string> enumerable)
        {
            return new StringEnumerableAssertions(enumerable);
        }

        public static StringAssertions Should(this string str)
        {
            return new StringAssertions(str);
        }

        public static BoolAssertions Should(this bool b)
        {
            return new BoolAssertions(b);
        }

        public static NullableBoolAssertions Should(this bool? b)
        {
            return new NullableBoolAssertions(b);
        }
    }

    public class JsonAssertions
    {
        public JsonAssertions And => this;

        public JsonAssertions(JToken subject)
        {
            Subject = subject;
        }

        public JToken Subject { get; }

        public JToken Which => Subject;

        public JsonAssertions HaveProperty(string expected)
        {
            var token = Subject[expected];
            Assert.True(token != null,
                $"Expected {Subject} to have property '{expected}'");

            return new JsonAssertions(token);
        }

        public SubjectWrapper<JToken> BeOfType<T>()
        {
            Assert.IsType<T>(Subject);
            return new SubjectWrapper<JToken>(Subject);
        }

        public JsonAssertions NotHaveProperty(string expected)
        {
            var token = Subject[expected];
            Assert.True(token == null,
                $"Expected {Subject} to have property '{expected}'");

            return this;
        }

        public SubjectWrapper<JToken> HavePropertyAsObject(string expected)
        {
            var prop = HaveProperty(expected);
            Assert.IsType<JObject>(prop.Subject);
            return new SubjectWrapper<JToken>(prop.Subject);
        }

        public JsonAssertions HavePropertyValue<T>(string expected, T value)
        {
            Assert.Equal(value, HaveProperty(expected).Subject.Value<T>());
            return this;
        }
    }

    public class CompilationLibraryArrayAssertions
    {
        public CompilationLibraryArrayAssertions And => this;

        public CompilationLibraryArrayAssertions(CompilationLibrary[] subject)
        {
            Subject = subject;
        }

        public CompilationLibrary[] Subject { get; }

        public CompilationLibraryArrayAssertions BeEquivalentTo(params string[] expected)
        {
            Assert.Equal(expected, Subject.Select(t => t.Name));
            return this;
        }
    }

    public class EnumerableAssertions<T>
    {
        public EnumerableAssertions<T> And => this;

        public EnumerableAssertions(IEnumerable<T> subject)
        {
            Subject = subject;
        }

        public IEnumerable<T> Subject { get; }

        public EnumerableAssertions<T> HaveCount(int expected)
        {
            Assert.Equal(expected, Subject.Count());
            return this;
        }

        public EnumerableAssertions<T> BeEmpty()
        {
            Assert.Empty(Subject);
            return this;
        }

        public SubjectWrapper<T> Contain(Func<T, bool> predicate)
        {
            foreach (var element in Subject)
            {
                if (predicate(element))
                {
                    return new SubjectWrapper<T>(element);
                }
            }
            Assert.Fail("No element matched predicate");
            return null;
        }

        public EnumerableAssertions<T> OnlyContain(Func<T, bool> predicate)
        {
            foreach (var element in Subject)
            {
                if (!predicate(element))
                {
                    Assert.Fail($"Element {element} did not match predicate");
                }
            }
            return this;
        }

        public virtual EnumerableAssertions<T> BeEquivalentTo(params T[] expected)
        {
            Assert.Equivalent(expected, Subject);
            return this;
        }
    }

    public class StringEnumerableAssertions : EnumerableAssertions<string>
    {
        public StringEnumerableAssertions(IEnumerable<string> subject) : base(subject)
        {
        }

        public StringEnumerableAssertions Contain(string expected)
        {
            Assert.Contains(expected, Subject);
            return this;
        }

        public override StringEnumerableAssertions BeEquivalentTo(params string[] expected)
        {
            Assert.Equivalent(expected, Subject);
            return this;
        }
    }

    public class SubjectWrapper<T>
    {
        public SubjectWrapper(T subject)
        {
            Subject = subject;
        }

        public T Subject { get; }

        public T Which => Subject;
    }

    public class StringAssertions
    {
        public StringAssertions And => this;

        public StringAssertions(string subject)
        {
            Subject = subject;
        }

        public string Subject { get; }

        public StringAssertions Be(string expected)
        {
            Assert.Equal(expected, Subject);
            return this;
        }

        public StringAssertions BeNull()
        {
            Assert.Null(Subject);
            return this;
        }

        public StringAssertions BeEmpty()
        {
            Assert.Empty(Subject);
            return this;
        }

        public StringAssertions Contain(string expected)
        {
            Assert.Contains(expected, Subject);
            return this;
        }
    }

    public class BoolAssertions
    {
        public BoolAssertions And => this;

        public BoolAssertions(bool subject)
        {
            Subject = subject;
        }

        public bool Subject { get; }

        public BoolAssertions Be(bool expected)
        {
            Assert.Equal(expected, Subject);
            return this;
        }

        public BoolAssertions BeTrue()
        {
            Assert.True(Subject);
            return this;
        }

        public BoolAssertions BeFalse()
        {
            Assert.False(Subject);
            return this;
        }
    }

    public class NullableBoolAssertions
    {
        public NullableBoolAssertions And => this;

        public NullableBoolAssertions(bool? subject)
        {
            Subject = subject;
        }

        public bool? Subject { get; }

        public NullableBoolAssertions Be(bool? expected)
        {
            Assert.Equal(expected, Subject);
            return this;
        }

        public NullableBoolAssertions BeTrue()
        {
            Assert.True(Subject);
            return this;
        }

        public NullableBoolAssertions BeFalse()
        {
            Assert.False(Subject);
            return this;
        }
    }
}
