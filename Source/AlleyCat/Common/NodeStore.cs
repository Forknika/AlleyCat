﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using EnsureThat;
using Godot;
using LanguageExt;
using Object = Godot.Object;

namespace AlleyCat.Common
{
    public class NodeStore<T> : Object
    {
        private readonly IDictionary<ulong, T> _store = new Dictionary<ulong, T>();

        public Option<T> Find(Node node)
        {
            Ensure.That(node, nameof(node)).IsNotNull();

            return _store.TryGetValue(node.GetInstanceId());
        }

        public T Get(Node node)
        {
            Ensure.That(node, nameof(node)).IsNotNull();

            return _store[node.GetInstanceId()];
        }

        public T Get(Node node, Func<Node, T> factory) => Find(node).IfNone(() =>
        {
            Ensure.That(factory, nameof(factory)).IsNotNull();

            var data = factory(node);

            Debug.Assert(data != null, "data != null");

            var id = node.GetInstanceId();

            node.OnTreeExiting().Subscribe(_ =>
            {
                (data as IDisposable)?.Dispose();

                _store.Remove(id);
            });

            _store.Add(node.GetInstanceId(), data);

            return data;
        });

        protected override void Dispose(bool disposing)
        {
            _store.Clear();

            base.Dispose(disposing);
        }
    }
}
