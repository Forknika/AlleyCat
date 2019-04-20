﻿using System.Collections;
using System.Linq;
using System.Reflection;
using AlleyCat.Common;
using AlleyCat.Game;
using EnsureThat;
using Godot;
using LanguageExt;
using static LanguageExt.Prelude;

namespace AlleyCat.Autowire
{
    public class NodeAttributeProcessor : InjectAttributeProcessor<NodeAttribute>
    {
        public Option<FieldInfo> NodePathField { get; }

        public NodeAttributeProcessor(MemberInfo member, NodeAttribute attribute) : base(member, attribute)
        {
        }

        public NodeAttributeProcessor(
            Option<FieldInfo> pathField,
            MemberInfo member,
            NodeAttribute attribute) : base(member, attribute)
        {
            NodePathField = pathField;
        }

        protected override IEnumerable GetDependencies(IAutowireContext context, Node node)
        {
            var path = FindNodePath(node);

            IEnumerable dependency;

            if (Enumerable)
            {
                var parent = path.Bind(v => Optional(node.GetNode(v))).IfNone(node);

                dependency = parent.GetChildren().Cast<Node>().Bind(c =>
                    DependencyType.IsInstanceOfType(c)
                        ? Some(c)
                        : Some(c).OfType<IGameObjectFactory>().Bind(f => f.Service.ToOption())).Freeze();
            }
            else
            {
                var targetPath = path.IfNone(() => NormalizeMemberName(Member.Name));

                var component = node.FindComponent<object>(targetPath);

                var matchingType = component.Filter(DependencyType.IsInstanceOfType);
                var fromFactory = component.OfType<IGameObjectFactory>().Bind(f => f.Service.ToOption());

                dependency = (matchingType | fromFactory.HeadOrNone()).Freeze();
            }

            return EnumerableHelper.Cast(dependency, DependencyType);
        }

        protected Option<NodePath> FindNodePath(Node node)
        {
            return NodePathField
                .Bind(f => Optional(f.GetValue(node)))
                .OfType<NodePath>()
                .HeadOrNone()
                .BiBind(Some,
                    () => Attribute.Path.Where(v => !string.IsNullOrEmpty(v)).Map(v => new NodePath(v)));
        }

        protected static string NormalizeMemberName(string name)
        {
            Ensure.That(name, nameof(name)).IsNotNullOrWhiteSpace();

            var normalized = name.StartsWith("_") ? name.Substring(1) : name;

            return normalized.Length < 2 ? normalized : normalized.Substring(0, 1).ToUpper() + normalized.Substring(1);
        }
    }
}
