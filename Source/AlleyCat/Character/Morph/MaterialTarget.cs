using System.Linq;
using EnsureThat;
using Godot;
using LanguageExt;
using static LanguageExt.Prelude;

namespace AlleyCat.Character.Morph
{
    public struct MaterialTarget
    {
        public string Material { get; }

        public Option<string> Mesh { get; }

        public MaterialTarget(string material, Option<string> mesh)
        {
            Ensure.That(material, nameof(material)).IsNotNull();

            Material = material;
            Mesh = mesh;
        }

        public Option<SpatialMaterial> FindMaterial(MeshInstance instance)
        {
            Ensure.That(instance, nameof(instance)).IsNotNull();

            var mesh = Mesh;
            var material = Material;

            var target = Optional(instance)
                .Filter(m => !mesh.Exists(name => name != m.Name))
                .Map(m => m.Mesh)
                .OfType<ArrayMesh>()
                .HeadOrNone();

            var indexes = target
                .Bind(m => Enumerable.Range(0, m.GetSurfaceCount()).Map(m.SurfaceGetName))
                .Map((index, name) => (name, index));

            var lookup = toMap(indexes);

            var result = 
                from m in target
                from i in lookup.Find(material)
                select m.SurfaceGetMaterial(i);

            return result.OfType<SpatialMaterial>().HeadOrNone();
        }

        public static MaterialTarget Create(string value)
        {
            Ensure.That(value, nameof(value)).IsNotEmptyOrWhitespace();

            var segments = value.Split(':');

            return segments.Length > 1
                ? new MaterialTarget(string.Join("", segments.Tail()), segments.HeadOrNone())
                : new MaterialTarget(value, None);
        }
    }
}
