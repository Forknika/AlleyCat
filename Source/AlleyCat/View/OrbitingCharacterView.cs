using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using AlleyCat.Character;
using AlleyCat.Common;
using AlleyCat.Control;
using AlleyCat.Event;
using AlleyCat.Game;
using AlleyCat.Logging;
using AlleyCat.Physics;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using Array = Godot.Collections.Array;
using static LanguageExt.Prelude;

namespace AlleyCat.View
{
    public class OrbitingCharacterView : OrbitingView, IThirdPersonView
    {
        public virtual Option<IHumanoid> Character
        {
            get => _character.Value;
            set => _character.OnNext(value);
        }

        public IObservable<Option<IHumanoid>> OnCharacterChange => _character.AsObservable();

        public Option<IEntity> FocusedObject { get; private set; }

        public IObservable<Option<IEntity>> OnFocusChange { get; }

        public float MaxFocalDistance
        {
            get => _maxFocalDistance;
            set => _maxFocalDistance = Mathf.Max(value, 0);
        }

        public float Convergence
        {
            get => _convergence;
            set => _convergence = Mathf.Max(value, 0);
        }

        public override Spatial Target => Camera;

        public override bool Valid => base.Valid && Character.IsSome;

        public bool AutoActivate => true;

        public override Vector3 Origin
        {
            get
            {
                var eyes = Character.Map(c => c.Vision.Viewpoint).IfNone(Vector3.Zero);
                var center = Character.Map(c => c.Center()).IfNone(Vector3.Zero);
                var distance = Distance;

                if (distance > Convergence)
                {
                    return center;
                }

                if (Convergence > 0)
                {
                    return eyes.LinearInterpolate(center, (distance - DistanceRange.Min) / Convergence);
                }

                return eyes;
            }
        }

        public override Vector3 Up => Vector3.Up;

        public override Vector3 Forward => Character
            .Map(c => c.GetGlobalTransform().Forward())
            .Map(new Plane(Vector3.Up, 0f).Project)
            .IfNone(Vector3.Forward);

        private float _maxFocalDistance = 2f;

        private float _convergence = 3f;

        private readonly BehaviorSubject<Option<IHumanoid>> _character;

        public OrbitingCharacterView(
            Camera camera,
            Option<IInputBindings> rotationInput,
            Option<IInputBindings> zoomInput,
            Range<float> yawRange,
            Range<float> pitchRange,
            Range<float> distanceRange,
            float initialDistance,
            Vector3 initialOffset,
            ProcessMode processMode,
            ITimeSource timeSource,
            bool active,
            ILoggerFactory loggerFactory) : base(
            camera,
            rotationInput,
            zoomInput,
            yawRange,
            pitchRange,
            distanceRange,
            initialDistance,
            initialOffset,
            processMode,
            timeSource,
            active,
            loggerFactory)
        {
            OnFocusChange = timeSource.OnPhysicsProcess
                .Where(_ => Active && Valid)
                .Select(_ => (Origin - Camera.GlobalTransform.origin).Normalized())
                .Select(direction => Origin + direction * MaxFocalDistance)
                .Select(to => Character
                    .Map(c => new Array {c.Spatial})
                    .Bind(v => Camera.GetWorld().IntersectRay(Origin, to, v)))
                .Select(hit => hit.Bind(h => h.GetCollider().FindEntity()))
                .Select(e => e.Filter(v => v.Valid && v.Visible))
                .DistinctUntilChanged();

            OnFocusChange
                .Do(v => this.LogDebug("Focusing on '{}'.", v))
                .TakeUntil(Disposed.Where(identity))
                .Subscribe(current => FocusedObject = current, this);

            _character = CreateSubject(Option<IHumanoid>.None);
        }

        protected override void PostConstruct()
        {
            base.PostConstruct();

            ZoomInput
                .Where(_ => Distance <= DistanceRange.Min)
                .Where(v => v > 0)
                .TakeUntil(Disposed.Where(identity))
                .Subscribe(_ => this.Deactivate(), this);
        }
    }
}
