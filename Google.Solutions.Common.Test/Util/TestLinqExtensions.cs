﻿//
// Copyright 2019 Google LLC
//
// Licensed to the Apache Software Foundation (ASF) under one
// or more contributor license agreements.  See the NOTICE file
// distributed with this work for additional information
// regarding copyright ownership.  The ASF licenses this file
// to you under the Apache License, Version 2.0 (the
// "License"); you may not use this file except in compliance
// with the License.  You may obtain a copy of the License at
// 
//   http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing,
// software distributed under the License is distributed on an
// "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY
// KIND, either express or implied.  See the License for the
// specific language governing permissions and limitations
// under the License.
//

using Google.Solutions.Common.Util;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;

namespace Google.Solutions.Common.Test.Util
{
    [TestFixture]
    public class TestLinqExtensions : FixtureBase
    {
        [Test]
        public void WhenEnumIsNull_EnsureNotNullReturnsEmpty()
        {
            IEnumerable<string> e = null;
            Assert.IsNotNull(e.EnsureNotNull());
            Assert.AreEqual(0, e.EnsureNotNull().Count());
        }

        [Test]
        public void WhenListsDontIntersect_ContainsAllIsFalse()
        {
            var list = new[] { "a", "b" };
            var lookup = new[] { "c", "d" };

            Assert.IsFalse(list.ContainsAll(lookup));
        }

        [Test]
        public void WhenListsPartiallyIntersect_ContainsAllIsFalse()
        {
            var list = new[] { "a", "b" };
            var lookup = new[] { "b", "c" };

            Assert.IsFalse(list.ContainsAll(lookup));
        }

        [Test]
        public void WhenListsOverlap_ContainsAllIsTrue()
        {
            var list = new[] { "a", "b", "c", "d" };
            var lookup = new[] { "c", "d" };

            Assert.IsTrue(list.ContainsAll(lookup));
        }
    }
}