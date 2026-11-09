using Adomeji.Pets.Surfaces;
using Adomeji.Composition;
using Adomeji.Pets.Physics;
using Adomeji.Pets.Rendering;
using Adomeji.Sprites.Configuration;
using Adomeji.Sprites.Loading;
using UnityEngine;
using UnityEngine.UI;
using Pose = Adomeji.Sprites.Loading.Pose;

namespace Adomeji.Pets.Actors
{
    // One desktop-pet. Walks along the bottom of the screen, climbs the side
    // walls, hangs from the ceiling, can be dragged with the mouse, and reacts
    // to level clear / death / pure perfect.
    //
    // No Update() here: the manager drives every pet from a single loop and
    // hands in per-frame data (dt, screen size, mouse) so it's read once, not
    // once per pet. UI component writes are dirty-checked so an idle pet costs
    // the canvas nothing.
    public class PetActor : MonoBehaviour
    {
        private static readonly IPetSurfaceQuery Surfaces = PetSurfaceQuery.Shared;

        private enum State
        {
            Fall,
            // Walk/Sit/Idle are what a pet DOES, not where: each runs the
            // same on the screen floor and on top of a ride. `Riding` picks
            // which surface the feet are measured against.
            Walk,
            Sit,
            Idle,      // standing around between strolls (idle / wave art)
            Stand,     // neutral fallback when walk/sit/idle are all disabled
            Climb,
            Ceiling,
            TileLeap,  // ballistic hop toward a ride (tile / planet / UI element)
            Celebrate,
            PPParty,
            Faint,
            Dragged,
        }

        private const float Gravity = 2500f;
        private const float WalkSpeed = 70f;
        private const float ClimbSpeed = 80f;
        private const float BasePixels = 64f;      // display size of the 16px art at scale 1
        private const float ThrowMax = 8000f;      // yank as hard as you like
        private const float Restitution = 0.65f;   // bounce energy kept per impact
        private const float BounceMinSpeed = 350f; // slower than this = land/cling, not bounce
        private const float TumbleSpeed = 700f;    // faster than this = tumbling, scared face
        private const float TerminalSpeed = 4000f; // fall speed cap, keeps wraps from tunneling
        private const float SteerSpeed = 900f;     // sideways drift while aiming at a tile
        private const float SteerAccel = 2000f;    // how fast that drift builds up
        private const float AimStraightSpeed = 80f; // sideways drift below this = "straight down"
        private const float LostGrace = 4f;        // wrap off: seconds off-screen before a pet is fetched back

        private PetView _view;
        // this pet's sprite pack, rolled at spawn from the selected packs
        private PackArt _art;

        private State _state = State.Fall;
        private float _stateTime;
        private float _decisionTimer;
        private Vector2 _pos;
        private Vector2 _vel;
        private int _dir = 1;        // 1 right, -1 left
        private int _climbSide = -1; // -1 left wall, 1 right wall
        private float _rotation;

        private float _animClock; // per-state clock; frame = clock * fps % count
        private float _bonkTime;  // just got hit by a planet: show the bonk face
        private float _panicTime; // doom incoming: tile scrolling away / planet inbound
        // stopped at a ledge (screen corner or tile lip) peering over it; the
        // pet holds still for this long, then turns back the way it came
        private float _edgeTime;
        // this idle is a wave rather than a plain stand-around
        private bool _waving;
        // cooldown after greeting someone, so a pair left overlapping (or a
        // pet stuck in a crowd) doesn't wave on a loop
        private float _greetCooldown;

        // panicking pets run 1.5x their usual pace
        private float PanicMul => _panicTime > 0f ? 1.5f : 1f;

        // Standing on a ride rather than the screen floor. State-gated: a
        // leap in flight, or an attachment left stale by a reaction, is not
        // something the pet is standing on.
        private bool Riding => _attach != null
            && (_state == State.Walk || _state == State.Sit || _state == State.Idle
                || _state == State.Stand);

        // queued reaction (small random stagger so pets don't move in lockstep)
        private State _pendingReaction = State.Fall;
        private bool _hasPendingReaction;
        private float _pendingDelay;

        private Vector2 _lastMouse;

        // riding: attached to anything with a Transform — a tile, a planet,
        // a piece of the game's UI. _attachCanvas != null means UI element.
        private Transform _attach;
        private Canvas _attachCanvas;
        private SpriteRenderer _attachSprite; // for real surface width of decorations
        private scrFloor _attachFloor;        // set when riding a tile; visibility watch
        private scrDecoration _attachDeco;    // set when riding a decoration; opacity watch
        private Renderer[] _attachDecoRenderers; // the deco's full visual, resolved at attach
        private float _decoHalfPx;            // surface half-width, refreshed by DecoCenter
        private int _rideSlot = -1;           // this frame's surface-cache slot for _attach
        private float _landCooldown;          // no insta-reattach after letting go
        private bool _aiming;                 // locked on to a landing spot mid-plummet
        private float _flingCooldown;         // one traffic accident at a time
        private float _lostTime;              // wrap off: time spent off-screen with no way back

        // short face-flash reacting to the player's judgements
        private Pose _emotePose;
        private float _emoteTime;
        private float _leapTime;
        private float _hangDuration;
        private float _rideTime;     // time on the CURRENT ride; sit/idle don't reset it
        private float _rideOffset;   // walking offset along the ride surface, px from center
        private int _floorIndex = -1; // listFloors index when riding a tile, else -1
        private bool _ridePlanet;    // planets are round: walk the circumference instead
        private float _rideAngle;    // radians around the planet center

        public void Init(int index)
        {
            _art = PlaceholderArt.RandomPack();
            _view = new PetView(GetComponent<RectTransform>(), GetComponent<Image>());

            RefreshSharedSize(); // spawning outside the manager's frame loop
            float half = Size() * 0.5f;
            float w = Surfaces.SpaceWidth, h = Surfaces.SpaceHeight;
            _pos = new Vector2(
                Random.Range(half + 20f, w - half - 20f),
                Random.Range(h * 0.4f, h * 0.8f));
            _dir = Random.value < 0.5f ? -1 : 1;
            _animClock = Random.Range(0f, 1f); // desync anim across the flock
            _decisionTimer = Random.Range(2f, 5f);
            SetState(State.Fall);
        }

        // Display size is settings scale x camera zoom — identical for every
        // pet, so the manager refreshes it once a frame instead of every pet
        // recomputing it for every hitbox/collision/draw query.
        private static float _sharedSize = BasePixels;

        public static void RefreshSharedSize()
        {
            float s = BasePixels * Mathf.Max(0.1f, RuntimeContext.Settings.Scale);
            if (RuntimeContext.Settings.ZoomScale) s *= Surfaces.ZoomFactor;
            _sharedSize = s;
        }

        private static float Size() => _sharedSize;

        // Driven by PetFlockController, once per frame. Grabbing is arbitrated by
        // the manager (see StartDrag) so overlapping pets can't all be picked
        // up by one click.
        public void Tick(float dt, float w, float h, Vector2 mouse, bool mouseHeld)
        {
            if (_view == null || !_view.IsAlive) return;

            float half = Size() * 0.5f;
            RefreshRideSlot();

            _stateTime += dt;
            _animClock += dt;
            if (_landCooldown > 0f) _landCooldown -= dt;
            if (_flingCooldown > 0f) _flingCooldown -= dt;
            if (_emoteTime > 0f) _emoteTime -= dt;
            if (_bonkTime > 0f) _bonkTime -= dt;
            if (_greetCooldown > 0f) _greetCooldown -= dt;
            if (_panicTime > 0f) _panicTime -= dt;
            if (_panicTime > 0f) _edgeTime = 0f; // no sightseeing while fleeing

            EnforceBehaviorSettings();

            // planet rides turned off mid-flight: step off whatever we're on
            if (_ridePlanet && !RuntimeContext.Settings.RidePlanets
                && _state != State.Dragged && _state != State.Faint)
            {
                DetachFromRide();
            }

            // a swinging planet clears everything in its path. Nothing is
            // swinging on most frames, so the whole test is skipped wholesale.
            if (Surfaces.AnyPlanetMoving
                && _flingCooldown <= 0f
                && _state != State.Fall && _state != State.Dragged && _state != State.Faint)
            {
                // only a planet rider can be standing on part of a planet, and
                // the exemption test costs a native IsChildOf per moving planet
                // — everyone else hands in nothing to exempt
                Transform ride = _ridePlanet ? _attach : null;
                if (Surfaces.TryPlanetHit(_pos.x, _pos.y, half, ride, out Vector2 fling))
                {
                    ClearAttachment();
                    _vel = fling;
                    _landCooldown = 0.8f; // fly properly before re-landing on the path
                    _flingCooldown = 1f;
                    _bonkTime = Allows(PackBehavior.Bonk) ? 1.1f : 0f;
                    _rotation = 0f;
                    SetColor(Color.white);
                    SetState(State.Fall);
                }
                // a planet is CLOSING IN but hasn't hit yet: see it coming, panic
                else if (Allows(PackBehavior.Panic)
                    && Surfaces.PlanetIncoming(_pos.x, _pos.y, half, ride,
                    out Vector2 threat))
                {
                    // run AWAY from the thing, not merely the other way: a planet
                    // closing from ahead turns the pet around, one chasing from
                    // behind keeps it sprinting forward. Ignore a threat sitting
                    // almost straight above/below so the pet doesn't jitter between
                    // sides while it passes overhead.
                    float dx = _pos.x - threat.x;
                    if (Mathf.Abs(dx) > half * 0.25f) _dir = dx >= 0f ? 1 : -1;
                    else if (_panicTime <= 0f) _dir = -_dir; // dead-on: any way but here
                    _panicTime = 0.3f; // refreshed every frame the threat persists
                }
            }
            HandlePendingReaction(dt);

            // an edge that stopped being solid drops whatever was standing on
            // or clinging to it (reactions are exempt so celebrations don't
            // hurl pets into the void)
            bool noFloor = Surfaces.NoFloorActive;
            bool noWall = Surfaces.NoWallActive;
            bool noCeiling = Surfaces.NoCeilingActive;
            if ((noFloor && !Riding && (_state == State.Walk || _state == State.Sit
                    || _state == State.Idle || _state == State.Stand))
                || (noWall && _state == State.Climb)
                || (noCeiling && _state == State.Ceiling))
                SetState(State.Fall);

            switch (_state)
            {
                case State.Fall: TickFall(dt, half, w, h); break;
                case State.Walk: TickWalk(dt, half, w, h); break;
                case State.Sit: TickSit(dt, half, w, h); break;
                case State.Idle: TickIdle(dt, half, w, h); break;
                case State.Stand: TickStand(dt, half, w, h); break;
                case State.Climb: TickClimb(dt, half, w, h); break;
                case State.Ceiling: TickCeiling(dt, half, w, h); break;
                case State.TileLeap: TickTileLeap(dt, half); break;
                case State.Celebrate: TickCelebrate(dt, half, 4f, false); break;
                case State.PPParty: TickCelebrate(dt, half, 6f, true); break;
                case State.Faint: TickFaint(dt, half); break;
                case State.Dragged: TickDragged(dt, mouse, mouseHeld); break;
            }

            // keep on screen — except while attached to a tile (allowed to
            // carry the pet toward the edge), or free-falling past an edge that
            // isn't solid any more (the void is a legitimate destination;
            // respawn handles it). Each edge is clamped on its own so turning
            // one mode on doesn't quietly open the others.
            if (!Riding && _state != State.TileLeap)
            {
                bool falling = _state == State.Fall;
                // a walker crossing a missing side wall wraps like a faller
                // does — clamping it would pin it to the edge instead
                if (!(noWall && (falling || _state == State.Walk)))
                    _pos.x = Mathf.Clamp(_pos.x, half, w - half);
                _pos.y = Mathf.Clamp(_pos.y,
                    falling && noFloor ? float.NegativeInfinity : half,
                    falling && noCeiling ? float.PositiveInfinity : h - half);
            }

            // Wrap off: a pet that left through a removed edge is out there on
            // its own. Off the TOP is fine and is the point of the setting —
            // gravity is already bringing it back — but out a side or through
            // the floor nothing hands it back, so after a grace period drop a
            // fresh one in from above. Riders/leapers are exempt: a tile is
            // allowed to carry a pet off-screen and back.
            bool lost = (!RuntimeContext.Settings.WrapWall && (_pos.x < -half || _pos.x > w + half))
                || (!RuntimeContext.Settings.WrapFloor && _pos.y < -half);
            if (lost && !Riding && _state != State.TileLeap && _state != State.Dragged)
            {
                _lostTime += dt;
                if (_lostTime > LostGrace) RespawnFromTop(half, w, h);
            }
            else _lostTime = 0f;

            Apply(half);
        }

        // ---------------- states ----------------

        private bool Allows(PackBehavior behavior) =>
            _art != null && _art.Allows(behavior);

        // Pack settings apply live. If a behavior is switched off while a pet
        // is doing it, leave that state through the same safe paths normal
        // gameplay uses instead of waiting for its timer to expire.
        private void EnforceBehaviorSettings()
        {
            if (_art == null) return;
            if (!Allows(PackBehavior.Edge)) _edgeTime = 0f;
            if (!Allows(PackBehavior.Panic)) _panicTime = 0f;
            if (!Allows(PackBehavior.Bonk)) _bonkTime = 0f;
            if (!Allows(PackBehavior.Judgement)) _emoteTime = 0f;

            switch (_state)
            {
                case State.Walk:
                    if (!Allows(PackBehavior.Walk)) ResumeGroundBehavior();
                    break;
                case State.Sit:
                    if (!Allows(PackBehavior.Sit)) ResumeGroundBehavior();
                    break;
                case State.Idle:
                    PackBehavior idleKind = _waving ? PackBehavior.Wave : PackBehavior.Idle;
                    if (!Allows(idleKind))
                    {
                        _waving = false;
                        ResumeGroundBehavior();
                    }
                    break;
                case State.Stand:
                    if (Allows(PackBehavior.Walk) || Allows(PackBehavior.Sit)
                        || (Allows(PackBehavior.Idle) && _art.HasArt(Pose.Idle)))
                        ResumeGroundBehavior();
                    break;
                case State.Climb:
                    if (!Allows(PackBehavior.Climb))
                    {
                        _dir = -_climbSide;
                        _vel = new Vector2(_dir * 60f, 0f);
                        _rotation = 0f;
                        _landCooldown = 0.6f;
                        SetState(State.Fall);
                    }
                    break;
                case State.Ceiling:
                    if (!Allows(PackBehavior.Ceiling))
                    {
                        _rotation = 0f;
                        _vel = Vector2.zero;
                        SetState(State.Fall);
                    }
                    break;
                case State.TileLeap:
                    if (!Allows(PackBehavior.Jump)) DetachFromRide();
                    break;
                case State.Celebrate:
                case State.PPParty:
                    if (!Allows(PackBehavior.Celebrate))
                    {
                        _rotation = 0f;
                        SetColor(Color.white);
                        SetState(State.Fall);
                    }
                    break;
                case State.Faint:
                    if (!Allows(PackBehavior.Death))
                    {
                        _rotation = 0f;
                        if (_pos.y > Size() * 0.5f) SetState(State.Fall);
                        else ResumeGroundBehavior();
                    }
                    break;
            }
        }

        // Pick the best enabled ground behavior. If all three are off, Stand
        // keeps the pet visible and physically valid without doing anything.
        private void ResumeGroundBehavior()
        {
            _waving = false;
            if (Allows(PackBehavior.Walk)) { SetState(State.Walk); return; }
            if (_state != State.Sit && Allows(PackBehavior.Sit))
            {
                SetState(State.Sit);
                return;
            }
            if (_state != State.Idle && Allows(PackBehavior.Idle)
                && _art.HasArt(Pose.Idle))
            {
                SetState(State.Idle);
                return;
            }
            if (Allows(PackBehavior.Sit)) { SetState(State.Sit); return; }
            if (Allows(PackBehavior.Idle) && _art.HasArt(Pose.Idle))
            {
                SetState(State.Idle);
                return;
            }
            _decisionTimer = Random.Range(1.5f, 3.5f);
            SetState(State.Stand);
        }

        private void ResumeFromRest(float half, float h)
        {
            if (!Allows(PackBehavior.Walk) && Allows(PackBehavior.Jump))
            {
                State before = _state;
                TryLeapToRide(half, h);
                if (_state != before) return;
            }
            ResumeGroundBehavior();
        }

        // Each screen edge is solid unless its mode turned it off. A missing
        // side wall wraps around (exit right, enter left); a missing floor is
        // the void, and a pet lost through it respawns from the top.
        private void TickFall(float dt, float half, float w, float h)
        {
            bool noFloor = Surfaces.NoFloorActive;
            bool noWall = Surfaces.NoWallActive;
            bool noCeiling = Surfaces.NoCeilingActive;

            _vel.y -= Gravity * dt;
            // wrapping through a missing floor forever would otherwise keep
            // adding gravity until the pet tunnels straight through the screen
            if (_vel.y < -TerminalSpeed) _vel.y = -TerminalSpeed;
            _vel.x *= 1f - 0.3f * dt; // light air drag

            // a plummet that hit terminal speed going STRAIGHT down (no
            // sideways drift already on it — a thrown pet keeps its arc) aims
            // itself at whatever tile is below instead of riding
            // the drop straight down. The sideways drift is accelerated into
            // and fades as the pet lines up, so it reads as a lean, not a
            // teleport; TryCatchWhileFalling below does the actual landing.
            if (RuntimeContext.Settings.FallSteer
                && _vel.y <= -TerminalSpeed && _landCooldown <= 0f
                && (_aiming || Mathf.Abs(_vel.x) <= AimStraightSpeed)
                && Surfaces.NearestFloorBelow(_pos.x, _pos.y, out float aimX))
            {
                _aiming = true;
                float pull = RuntimeContext.Settings.FallSteerScale;
                float dx = aimX - _pos.x;
                float want = Mathf.Clamp(dx * 2f, -SteerSpeed * pull, SteerSpeed * pull);
                _vel.x = Mathf.MoveTowards(_vel.x, want, SteerAccel * pull * dt);
                if (Mathf.Abs(dx) > half) _dir = dx >= 0f ? 1 : -1;
            }

            float prevX = _pos.x;
            float prevBottom = _pos.y - half;
            _pos += _vel * dt;

            // anything passing under our feet is a valid place to live
            if (TryCatchWhileFalling(half, prevX, prevBottom)) return;

            // tumble when flying fast, straighten up when slow
            float speedSq = _vel.sqrMagnitude;
            if (speedSq > TumbleSpeed * TumbleSpeed)
                _rotation += (_vel.x >= 0f ? -1f : 1f) * Mathf.Sqrt(speedSq) * 0.5f * dt;
            else
                _rotation = Mathf.MoveTowards(_rotation, 0f, 720f * dt);

            if (!noWall)
            {
                // left wall
                if (_pos.x <= half && _vel.x < 0f)
                {
                    _pos.x = half;
                    if (-_vel.x > BounceMinSpeed) _vel.x = -_vel.x * Restitution; // boing
                    else if (CanGrabWall && Random.value < 0.6f) { StartClimb(-1, half); return; }
                    else _vel.x = 0f;
                }
                // right wall
                else if (_pos.x >= w - half && _vel.x > 0f)
                {
                    _pos.x = w - half;
                    if (_vel.x > BounceMinSpeed) _vel.x = -_vel.x * Restitution;
                    else if (CanGrabWall && Random.value < 0.6f) { StartClimb(1, half); return; }
                    else _vel.x = 0f;
                }
            }

            // ceiling: bounce off hard hits, cling on soft ones
            if (!noCeiling && _pos.y >= h - half && _vel.y > 0f)
            {
                _pos.y = h - half;
                if (!Allows(PackBehavior.Ceiling)
                    || (_vel.y > BounceMinSpeed && Random.value < 0.6f))
                {
                    _vel.y = -_vel.y * Restitution;
                }
                else
                {
                    _dir = _vel.x >= 0f ? 1 : -1;
                    _vel = Vector2.zero;
                    _rotation = CeilingRotation();
                    SetState(State.Ceiling);
                    return;
                }
            }

            // floor: bounce until too slow to bounce, then land
            if (!noFloor && _pos.y <= half && _vel.y <= 0f)
            {
                _pos.y = half;
                if (-_vel.y > BounceMinSpeed)
                {
                    _vel.y = -_vel.y * Restitution;
                    _vel.x *= 0.8f; // ground friction per bounce
                }
                else
                {
                    _vel = Vector2.zero;
                    _rotation = 0f;
                    // barely touched down before eyeing the next thing to climb
                    _decisionTimer = Random.Range(0.6f, 1.8f);
                    ResumeGroundBehavior();
                }
            }

            // out through a missing edge: wrap around — thrown off the right,
            // pops in from the left with its speed and spin intact; dropped
            // through the floor, comes back in falling from the top; flung up
            // through the ceiling, comes up out of the floor. Top/bottom share
            // the vertical portal switch; left/right share the horizontal one.
            // With a portal off the pet simply leaves — a fling up through an
            // open ceiling is a real arc that has to come back down (Tick's
            // lost check fetches back the ones nothing can return).
            if (noWall && RuntimeContext.Settings.WrapWall)
                _pos.x = PortalMath.Wrap(_pos.x, half, w);

            if (noFloor && RuntimeContext.Settings.WrapFloor && _pos.y < -half)
                _pos.y = PortalMath.Wrap(_pos.y, half, h);
            else if (noCeiling && RuntimeContext.Settings.WrapCeiling && _pos.y > h + half)
                _pos.y = PortalMath.Wrap(_pos.y, half, h);
        }

        // Falling: catch whatever's under our feet — tile, planet, decoration,
        // UI. Shared by both modes; cooldown stops let-go-and-regrab loops.
        private bool TryCatchWhileFalling(float half, float prevX, float prevBottom)
        {
            if (_landCooldown > 0f || _vel.y >= 0f) return false;
            float newBottom = _pos.y - half;

            if (Surfaces.TryLandOnFloor(_pos.x, prevX, prevBottom, newBottom, half,
                    out Transform floor, out int floorIndex, out float offset))
            {
                Attach(floor, null);
                _floorIndex = floorIndex;
                _rideOffset = offset;
                LandOnRide(Random.Range(10f, 20f));
                return true;
            }
            if (Surfaces.TryLandOnDeco(_pos.x, prevBottom, newBottom, half,
                    out Transform deco, out float decoOffset))
            {
                Attach(deco, null);
                _rideOffset = decoOffset;
                LandOnRide(Random.Range(8f, 16f));
                return true;
            }
            if (Surfaces.TryLandOnPlanet(_pos.x, _pos.y, half,
                    out Transform planet, out float angle))
            {
                Attach(planet, null);
                _ridePlanet = true;
                _rideAngle = angle;
                LandOnRide(Random.Range(6f, 14f));
                return true;
            }
            return false;
        }

        private void LandOnRide(float duration)
        {
            _hangDuration = duration;
            _vel = Vector2.zero;
            _rotation = 0f;
            ResumeGroundBehavior();
        }

        private void TickWalk(float dt, float half, float w, float h)
        {
            // a stroll along a ride is the same stroll, measured along the
            // ride's surface instead of the screen floor
            if (Riding)
            {
                if (TickRide(dt, half, w, h, true)) RollDecision(dt, half, h);
                return;
            }

            _pos.y = half;

            // peering over the screen's edge: stand still, look down, then
            // head back inland
            if (_edgeTime > 0f)
            {
                _edgeTime -= dt;
                if (_edgeTime <= 0f) _dir = -_dir;
                return;
            }

            _pos.x += _dir * WalkSpeed * PanicMul * dt;

            RollDecision(dt, half, h);

            // no side walls: walk straight off the edge and back in the other
            // side. Grabbing a wall that isn't there would be undone by the
            // no-wall check at the top of Tick the very next frame, and the
            // pet would flicker climb/walk forever against the screen edge.
            if (Surfaces.NoWallActive)
            {
                if (!RuntimeContext.Settings.WrapWall) { }  // walks off and keeps going
                else if (_pos.x < -half) _pos.x += w + half * 2f;
                else if (_pos.x > w + half) _pos.x -= w + half * 2f;
            }
            else if (_pos.x <= half + 1f)
            {
                if (CanGrabWall && Random.value < 0.75f) StartClimb(-1, half);
                else if (_dir >= 0 || !StartPeek()) _dir = 1;
            }
            else if (_pos.x >= w - half - 1f)
            {
                if (CanGrabWall && Random.value < 0.75f) StartClimb(1, half);
                else if (_dir <= 0 || !StartPeek()) _dir = -1;
            }
        }

        // What to do next mid-stroll. Same repertoire wherever the pet is
        // standing: sit down, stand around, turn back, or hop onto something.
        // Hopping is the main hobby; walking is the commute.
        private void RollDecision(float dt, float half, float h)
        {
            _decisionTimer -= dt;
            if (_decisionTimer > 0f || _panicTime > 0f) return; // fleeing pets don't sightsee
            _decisionTimer = Random.Range(1.5f, 3.5f);
            float roll = Random.value;
            if (roll < 0.15f)
            {
                if (Allows(PackBehavior.Sit)) SetState(State.Sit);
            }
            else if (roll < 0.25f)
            {
                if (Allows(PackBehavior.Idle)) StartIdle();
            }
            else if (roll < 0.40f) _dir = -_dir;
            else if (roll < 0.75f && Allows(PackBehavior.Jump)) TryLeapToRide(half, h);
        }

        private void TickSit(float dt, float half, float w, float h)
        {
            if (Riding && !TickRide(dt, half, w, h, false)) return;
            if (_stateTime > 3f)
                ResumeFromRest(half, h);
        }

        // Stand still for a beat. A pack that draws no idle art just sits,
        // which is what pets did before. Waving is not rolled here: it only
        // happens at another pet (see StartGreet).
        private void StartIdle()
        {
            if (!Allows(PackBehavior.Idle) || !_art.HasArt(Pose.Idle)) return;
            SetState(State.Idle);
            _waving = false;
        }

        // Walked into another pet: stop and wave at it instead of strolling
        // off. Needs wave art; the cooldown keeps a crowded corner from
        // turning into a wave loop. faceDir points back at whoever it bumped.
        private bool StartGreet(int faceDir)
        {
            if (_greetCooldown > 0f || _panicTime > 0f) return false;
            if (!Allows(PackBehavior.Wave) || !_art.HasArt(Pose.Wave)) return false;
            _greetCooldown = Random.Range(6f, 12f);
            SetState(State.Idle);
            _dir = faceDir;
            _waving = true;
            return true;
        }

        private void TickIdle(float dt, float half, float w, float h)
        {
            if (Riding && !TickRide(dt, half, w, h, false)) return;
            if (_stateTime > (_waving ? 2f : 3.5f))
                ResumeFromRest(half, h);
        }

        private void TickStand(float dt, float half, float w, float h)
        {
            if (Riding)
            {
                if (!TickRide(dt, half, w, h, false)) return;
            }
            else _pos.y = half;

            _decisionTimer -= dt;
            if (_decisionTimer > 0f || !Allows(PackBehavior.Jump)) return;
            _decisionTimer = Random.Range(1.5f, 3.5f);
            TryLeapToRide(half, h);
        }

        // A pet that just let go of a wall walks away from it before it may
        // grab again — without the pause it lands at the foot of the wall
        // still facing it, re-grabs on the next frame, and a corner (nowhere
        // to walk off to) turns that into a climb/walk flicker.
        private bool CanGrabWall => _landCooldown <= 0f && Allows(PackBehavior.Climb);
        private bool CanGrabCeiling => _landCooldown <= 0f && Allows(PackBehavior.Ceiling);

        // Stop at a ledge and look over it before turning around. Callers only
        // ask while the pet is still heading INTO the edge — one that already
        // turned away would otherwise peek again on the spot, facing inland.
        // Packs with no edge art skip it and turn as they always did.
        private bool StartPeek()
        {
            if (_edgeTime > 0f || _panicTime > 0f
                || !Allows(PackBehavior.Edge) || !_art.HasArt(Pose.Edge)) return false;
            if (Random.value >= 0.5f) return false;
            _edgeTime = Random.Range(1.2f, 2.5f);
            _animClock = 0f;
            return true;
        }

        private void StartClimb(int side, float half)
        {
            _climbSide = side;
            _pos.x = side < 0 ? half : Surfaces.SpaceWidth - half;
            _vel = Vector2.zero;
            // fresh timer: Walk's is held at <= 0 for as long as the pet is
            // panicking, and inheriting that drops it off the wall instantly
            _decisionTimer = Random.Range(2f, 5f);
            SetState(State.Climb);
        }

        private void TickClimb(float dt, float half, float w, float h)
        {
            _pos.x = _climbSide < 0 ? half : w - half;
            _pos.y += ClimbSpeed * dt;
            _rotation = ClimbRotation();

            _decisionTimer -= dt;
            if (_decisionTimer <= 0f)
            {
                _decisionTimer = Random.Range(2f, 5f);
                if (Random.value < 0.3f)
                {
                    // let go and hop off the wall, facing away from it so the
                    // landing walks back out instead of into the same wall
                    _vel = new Vector2(-_climbSide * Random.Range(150f, 350f), 100f);
                    _dir = -_climbSide;
                    _rotation = 0f;
                    _landCooldown = 0.6f;
                    SetState(State.Fall);
                    return;
                }
            }

            if (_pos.y >= h - half)
            {
                _pos.y = h - half;
                _dir = -_climbSide; // crawl toward screen center
                if (Allows(PackBehavior.Ceiling))
                {
                    _rotation = CeilingRotation();
                    SetState(State.Ceiling);
                }
                else
                {
                    _rotation = 0f;
                    _vel = new Vector2(_dir * 60f, 0f);
                    _landCooldown = 0.6f;
                    SetState(State.Fall);
                }
            }
        }

        // Packs that draw their own climb/ceiling art (every PetActor-format
        // pack does) already have it oriented — spinning the sprite as well
        // would land it sideways. Everyone else keeps the rotated walk art.
        private float ClimbRotation() =>
            _art.Oriented(Pose.Climb) ? 0f : (_climbSide < 0 ? -90f : 90f);

        private float CeilingRotation() =>
            _art.Oriented(Pose.Ceiling) ? 0f : 180f;

        // Hanging is hanging: the pet clings where it grabbed on and stays
        // put until it lets go. No crawling along the ceiling.
        private void TickCeiling(float dt, float half, float w, float h)
        {
            _pos.y = h - half;
            _pos.x = Mathf.Clamp(_pos.x, half, w - half);
            _rotation = CeilingRotation();

            if (_stateTime > 5f)
            {
                _rotation = 0f;
                _vel = new Vector2(_dir * 60f, 0f);
                SetState(State.Fall);
            }
        }

        // ---------------- riding (tiles, planets, UI, whatever) ----------------

        // From Walk: pick something on screen and hop onto it. Anything goes:
        // tiles, planets, decorations, the game's own UI. All picks come from
        // the manager's already-projected surface cache — zero searching here.
        private void TryLeapToRide(float half, float h)
        {
            if (!Allows(PackBehavior.Jump)) return;
            // the ceiling is a ride too: a straight-up hop that arrives with
            // almost no speed left, which is what the cling branch in TickFall
            // wants (a fast arrival bounces off instead).
            if (CanGrabCeiling
                && !Surfaces.NoCeilingActive
                && Random.value < 0.12f && LeapToCeiling(half, h)) return;

            // tiles are home turf; decorations are sightseeing; planets are tourism
            float roll = Random.value;
            bool found = roll < 0.55f ? PickTile()
                       : roll < 0.80f ? PickDeco()
                       : PickPlanet();
            if (!found) found = PickTile();
            if (!found) found = PickDeco();
            if (!found) return;

            _leapTime = 0.7f;
            // tile rides can turn into a whole stroll along the path, give them time
            _hangDuration = _floorIndex >= 0 ? Random.Range(10f, 20f) : Random.Range(6f, 14f);
            SetState(State.TileLeap);
        }

        // Straight-up hop at the ceiling. Solve the launch speed for the gap so
        // the pet tops out just under it: TickFall's ceiling check then takes
        // the cling path, no new state needed.
        private bool LeapToCeiling(float half, float h)
        {
            float dist = (h - half) - _pos.y;
            if (dist < half) return false; // already up there
            // leftover climb speed at the ceiling: high enough that the pet is
            // still moving up on the frame it gets there (a whisker of speed
            // can apex a frame early and drop back), under BounceMinSpeed so
            // it clings instead of bouncing
            const float arrive = 250f;
            _vel = new Vector2(_dir * WalkSpeed,
                Mathf.Sqrt(2f * Gravity * dist + arrive * arrive));
            _rotation = 0f;
            _landCooldown = 0.2f; // don't grab a tile on the way past
            SetState(State.Fall);
            return true;
        }

        private bool PickTile()
        {
            if (!Surfaces.RandomFloor(out Transform t, out int idx)) return false;
            Attach(t, null);
            _floorIndex = idx; // enables walking tile-to-tile along the path
            _rideOffset = RideCenterOffset(); // start on the slab, not the node
            return true;
        }

        private bool PickDeco()
        {
            if (!Surfaces.RandomDeco(out Transform t)) return false;
            Attach(t, null);
            return true;
        }

        private bool PickPlanet()
        {
            if (!Surfaces.RandomPlanet(out Transform t)) return false;
            Attach(t, null);
            _ridePlanet = true;
            return true;
        }

        private bool Attach(Transform target, Canvas canvas)
        {
            _attach = target;
            _attachCanvas = canvas;
            _attachSprite = canvas == null && target != null
                ? target.GetComponent<SpriteRenderer>() : null;
            _attachFloor = canvas == null && target != null
                ? target.GetComponent<scrFloor>() : null;
            // deco attach targets the decoration root; opacity (where event
            // fades actually land) lives on the scrDecoration, and the visual
            // is a SET of renderers (floor objects are tile + borders + icon)
            _attachDeco = canvas == null && target != null
                ? target.GetComponentInParent<scrDecoration>() : null;
            _attachDecoRenderers = _attachDeco != null
                ? _attachDeco.GetComponentsInChildren<Renderer>() : null;
            _floorIndex = -1;
            _rideOffset = 0f;
            _ridePlanet = false;
            _rideAngle = Mathf.PI / 2f; // start on top
            _rideTime = 0f;
            _decisionTimer = Random.Range(1.5f, 3.5f); // settle in before deciding anything
            RefreshRideSlot(); // metrics are read before the next tick resolves it
            return true;
        }

        // Where the current ride sits in this frame's surface cache, resolved
        // once per tick. Everything downstream (position, metrics, surface
        // height, neighbors) then reads by index instead of re-projecting the
        // transform or scanning the cache — the difference between O(1) and
        // O(tiles) per query, times a hundred pets.
        private void RefreshRideSlot()
        {
            _rideSlot = _attach == null ? -1
                : _attachFloor != null ? Surfaces.FloorSlot(_floorIndex, _attach)
                : _ridePlanet ? Surfaces.PlanetSlot(_attach)
                : _attachCanvas == null && _attachDeco != null
                    ? Surfaces.DecoSlot(_attach)
                : -1;
        }

        private bool ScreenPosOf(Transform t, Canvas canvas, out Vector2 screen)
        {
            screen = default;
            if (t == null || !t.gameObject.activeInHierarchy) return false;
            if (canvas != null)
            {
                Camera uiCam = canvas.renderMode == RenderMode.ScreenSpaceOverlay
                    ? null : canvas.worldCamera;
                screen = RectTransformUtility.WorldToScreenPoint(uiCam, t.position);
                return true;
            }
            var cam = Surfaces.GameCamera();
            if (cam == null) return false;
            Vector3 sp = cam.WorldToScreenPoint(t.position);
            if (sp.z < 0f) return false;
            screen = new Vector2(Surfaces.DisplayMapX(sp.x, sp.x),
                Surfaces.DisplayMapY(sp.y, sp.y));
            return true;
        }

        private bool RideScreenPos(out Vector2 screen)
        {
            // the cache already projected every surface this frame
            if (_rideSlot >= 0)
            {
                screen = _attachFloor != null ? Surfaces.FloorScreenPos(_rideSlot)
                    : _ridePlanet ? Surfaces.PlanetScreenPos(_rideSlot)
                    : Surfaces.DecoScreenPos(_rideSlot);
                return true;
            }
            screen = default;
            if (_attach == null) return false; // Unity null: also true once destroyed
            return ScreenPosOf(_attach, _attachCanvas, out screen);
        }

        // Riding a decoration (not a tile, not a planet, not UI)? Those are
        // measured off the rendered bounds, because editor pivots are
        // arbitrary — the transform can sit nowhere near the visible prop.
        private bool IsDecoRide =>
            !_ridePlanet && _attachCanvas == null
            && _attachFloor == null && _attachDeco != null;

        // Replace the transform-pivot center with the rendered-bounds surface:
        // x = bounds center, y = bounds top edge. Refreshes _decoHalfPx as a
        // side effect. False = the decoration is gone/hidden and can't be
        // stood on anymore.
        private bool DecoCenter(ref Vector2 center)
        {
            // cached: the surface pass already measured and projected the
            // bounds this frame (and dropped anything faded, hidden or gone)
            if (_rideSlot >= 0)
            {
                center = Surfaces.DecoScreenPos(_rideSlot);
                _decoHalfPx = Surfaces.DecoHalfWidth(_rideSlot);
                return true;
            }
            // event fades live on the decoration's own opacity, not the renderers
            if (_attachDecoRenderers == null
                || !Surfaces.DecoOpaque(_attachDeco)) return false;
            if (!Surfaces.CombinedBounds(_attachDecoRenderers, out Bounds bnd))
                return false;
            var cam = Surfaces.GameCamera();
            if (cam == null) return false;
            Vector3 c = cam.WorldToScreenPoint(bnd.center);
            if (c.z < 0f) return false;
            Vector3 top = cam.WorldToScreenPoint(
                new Vector3(bnd.center.x, bnd.max.y, bnd.center.z));
            center = new Vector2(Surfaces.DisplayMapX(c.x, c.x),
                Surfaces.DisplayMapY(top.y, c.y));
            _decoHalfPx = Mathf.Max(8f, bnd.extents.x * Surfaces.PixelsPerUnit
                * Surfaces.DisplayScaleX);
            return true;
        }

        // Homing ballistic hop: re-solve launch velocity for the remaining
        // flight time each frame, so a moving camera or orbiting planet
        // can't make us miss.
        private void TickTileLeap(float dt, float half)
        {
            if (!RideScreenPos(out Vector2 target)) { DetachFromRide(); return; }
            bool deco = IsDecoRide;
            if (deco && !DecoCenter(ref target)) { DetachFromRide(); return; }
            // land standing on top of the ride (deco y is already the top edge)
            target.x += RideCenterOffset();
            target.y += half + RideTopOffset(deco);

            _leapTime -= dt;
            float t = Mathf.Max(_leapTime, 0.05f);
            _vel = (target - _pos) / t + new Vector2(0f, 0.5f * Gravity * t);

            _vel.y -= Gravity * dt;
            _pos += _vel * dt;
            _rotation = 0f;

            if (_leapTime <= 0f || (target - _pos).sqrMagnitude < 25f)
            {
                _pos = target;
                _vel = Vector2.zero;
                ResumeGroundBehavior();
            }
        }

        // Keep a rider on its ride: track the ride's screen position, put the
        // feet on its surface, and run the upkeep every rider needs (the ride
        // vanishing, scrolling away, outstaying its welcome). `move` = the pet
        // is strolling; one that sat down or stopped to wave holds its spot.
        // On tiles, walking off the edge continues onto the next tile of the
        // path. False = the pet let go, and the caller must return at once.
        private bool TickRide(float dt, float half, float w, float h, bool move)
        {
            _rideTime += dt;
            if (!RideScreenPos(out Vector2 center)) { DetachFromRide(); return false; }

            // the tile we're standing on played its disappear animation:
            // there is no longer a tile. Gravity has opinions about that.
            // A decoration tile answers to its decoration's opacity instead —
            // the inner scrFloor's own fade state doesn't apply to it.
            // Being in the cache is already proof of visibility: the surface
            // pass ran this exact test on it this frame.
            if (_rideSlot < 0 && _attachFloor != null
                && !(_attachDeco != null ? Surfaces.DecoOpaque(_attachDeco)
                                         : Surfaces.FloorVisible(_attachFloor)))
            {
                DetachFromRide();
                return false;
            }

            // decoration rides stand on the sprite bounds; a decoration that
            // faded out / got hidden is likewise no longer ground
            bool decoRide = IsDecoRide;
            if (decoRide && !DecoCenter(ref center)) { DetachFromRide(); return false; }

            if (_ridePlanet)
            {
                // round ride: walk laps around the circumference while the
                // planet orbits — feet always pointed at the center. The sprite
                // is rotated by (angle - 90°), which points its local +x (the
                // facing direction) toward DECREASING angle — so _dir = 1 must
                // decrease the angle or the pet moonwalks around the planet.
                float standRadius = RideSurfaceHalfWidth() + half * 0.8f;
                if (move)
                    _rideAngle -= _dir * (WalkSpeed * 0.8f * PanicMul * dt) / Mathf.Max(standRadius, 1f);
                _pos = center + new Vector2(Mathf.Cos(_rideAngle), Mathf.Sin(_rideAngle)) * standRadius;
                _rotation = _rideAngle * Mathf.Rad2Deg - 90f;
            }
            else
            {
                float surfHalf = RideSurfaceHalfWidth();
                // peering over the tile's lip: hold the offset, then turn back
                if (_edgeTime > 0f)
                {
                    _edgeTime -= dt;
                    if (_edgeTime <= 0f) _dir = -_dir;
                }
                else if (move) _rideOffset += _dir * WalkSpeed * 0.8f * PanicMul * dt;

                // tiles: the path is a polyline through tile centers, and tiles
                // can slant — standing at the anchor's center HEIGHT while
                // offset sideways in screen-x means hovering in open air. So
                // follow the segment toward whichever neighbor the offset
                // points at: interpolate the stand height along it, and hand
                // anchorship to the neighbor at the segment midpoint.
                float standY = center.y;
                bool fwdRamp = false; // walking toward a reachable neighbor
                int seamNbSlot = -1;  // neighbor tile, for surface probing at the seam
                if (_attachFloor != null && _rideSlot >= 0 && _floorIndex >= 0
                    && Surfaces.NeighborSlot(_rideSlot, (int)Mathf.Sign(_rideOffset),
                        out Vector2 nb, out int nbSlot)
                    && (seamNbSlot = nbSlot) >= 0)
                {
                    float dx = nb.x - center.x;
                    if (Mathf.Abs(dx) > 4f) // straight-up neighbors aren't walkable
                    {
                        float t = Mathf.Clamp01(_rideOffset / dx);
                        standY = Mathf.Lerp(center.y, nb.y, t);
                        fwdRamp = (int)Mathf.Sign(_rideOffset) == _dir;

                        if (fwdRamp && t >= 0.5f)
                        {
                            // crossed onto the neighbor's half of the segment:
                            // re-anchor there, keeping the on-screen position
                            // continuous (standY for this frame is the same
                            // point on the same segment either way)
                            float petX = center.x + _rideOffset;
                            _attach = Surfaces.FloorTransform(nbSlot);
                            _attachSprite = null; // floor rides measure off the cache
                            _attachFloor = Surfaces.FloorOf(nbSlot);
                            _floorIndex = Surfaces.FloorIndexOf(nbSlot);
                            _rideSlot = nbSlot;
                            seamNbSlot = -1; // the seam neighbor IS us now
                            _rideOffset = petX - nb.x;
                            center = nb;
                        }
                    }
                }

                // the walkable span is centred on the slab, which need not sit
                // exactly on the node the path polyline runs through
                float cOff = RideCenterOffset();

                // reached the LEADING edge with nowhere to continue (no
                // neighbor, a gap, or a straight-up tile): turn back or hop off
                if (move && _edgeTime <= 0f && !fwdRamp && (_rideOffset - cOff) * _dir > surfHalf)
                {
                    if (Random.value < 0.25f) { DetachFromRide(); return false; } // wander off the edge
                    _rideOffset = cOff + _dir * surfHalf;
                    if (!StartPeek()) _dir = -_dir;
                }

                // deco center.y is the surface top itself; tile center.y is the
                // node, so lift by the tile's measured half-thickness. When the
                // tile's own collider is available, its surface height at the
                // pet's exact x wins — that's what keeps feet ON slanted slabs
                // and L-shaped corner tiles instead of on their bounding box.
                float walkX = center.x + _rideOffset;
                // upright sprite on a slope: the surface rises across the
                // sprite's width, so resting only the CENTER on it sinks the
                // uphill half into the slab — probe under the whole footprint
                // (both sides, and the neighbor tile near a seam) and rest on
                // the HIGHEST surface found. Ramp fallback only when no
                // surface answers at all.
                float footY;
                float best = float.NegativeInfinity;
                if (_attachFloor != null && _rideSlot >= 0)
                {
                    float side = half * 0.9f;
                    ProbeSurface(_rideSlot, walkX, side, ref best);
                    if (seamNbSlot >= 0) ProbeSurface(seamNbSlot, walkX, side, ref best);
                }
                if (!float.IsNegativeInfinity(best))
                    footY = best + 2f;
                else
                    footY = standY + RideTopOffset(decoRide);
                _pos = new Vector2(walkX, footY + half);
                _rotation = 0f;
            }

            // riding a tile that's about to scroll off the screen: the pet can
            // see the edge coming — panic and sprint back toward safety
            if (_attachFloor != null && Allows(PackBehavior.Panic))
            {
                float m = Mathf.Max(35f, Surfaces.PixelsPerUnit * 0.7f);
                bool nearEdge = center.x < m || center.x > w - m
                             || center.y < m || center.y > h - m;
                if (nearEdge)
                {
                    // run away from whichever side edge is closest
                    if (center.x < m) _dir = 1;
                    else if (center.x > w - m) _dir = -1;
                    _panicTime = 0.2f; // refreshed while the edge keeps closing in
                }
            }

            bool offScreen = center.x < -20f || center.x > w + 20f
                          || center.y < -20f || center.y > h + 20f;

            // ride carried us off screen (or nearly): let go and fall back in
            if (offScreen || _rideTime > _hangDuration)
            {
                DetachFromRide();
                return false;
            }
            return true;
        }

        // Highest tile surface under the pet's footprint: probes center and
        // both foot edges on the given cached tile, folding results into `best`.
        private static void ProbeSurface(int slot, float x, float side, ref float best)
        {
            if (Surfaces.FloorSurfaceTopAt(slot, x, out float t) && t > best) best = t;
            if (Surfaces.FloorSurfaceTopAt(slot, x - side, out t) && t > best) best = t;
            if (Surfaces.FloorSurfaceTopAt(slot, x + side, out t) && t > best) best = t;
        }

        // Gap between the ride's anchor point and the surface the pet's feet
        // rest on, in screen pixels. Decoration anchors are already the top
        // edge; a tile anchor is the node at the middle of the slab.
        private float RideTopOffset(bool decoRide)
        {
            if (decoRide) return 2f;
            if (_attachFloor != null)
            {
                Surfaces.FloorMetrics(_rideSlot, out _, out float topOff, out _);
                return topOff;
            }
            return 6f;
        }

        // Screen-x gap between the ride's anchor and the middle of the surface.
        private float RideCenterOffset()
        {
            if (_attachFloor == null) return 0f;
            Surfaces.FloorMetrics(_rideSlot, out _, out _, out float cOff);
            return cOff;
        }

        // Half-width of the walkable surface in screen pixels.
        private float RideSurfaceHalfWidth()
        {
            if (_attachCanvas != null)
            {
                var rt = _attach as RectTransform;
                if (rt != null) return Mathf.Max(12f, rt.rect.width * 0.5f * rt.lossyScale.x);
                return 30f;
            }
            // tiles: measured footprint of this tile's renderer. Tiles are
            // narrower than the gap between nodes, so anything wider walks the
            // pet off the end and out over nothing.
            if (_attachFloor != null)
            {
                Surfaces.FloorMetrics(_rideSlot, out float tileHalf, out _, out _);
                return tileHalf;
            }
            // decoration ride: DecoCenter just measured the combined bounds
            if (IsDecoRide) return _decoHalfPx;
            // planets: the same solid radius the landing check uses
            if (_ridePlanet && _rideSlot >= 0)
                return Surfaces.PlanetRadius(_rideSlot);
            // planets that dropped out of the cache: real sprite bounds
            if (_attachSprite != null)
                return Mathf.Max(8f, _attachSprite.bounds.extents.x * Surfaces.PixelsPerUnit
                    * Surfaces.DisplayScaleX);
            // bare world object: project a half-unit sideways step
            var cam = Surfaces.GameCamera();
            if (cam == null) return 30f;
            Vector3 p = _attach.position;
            Vector3 a = cam.WorldToScreenPoint(p);
            Vector3 b = cam.WorldToScreenPoint(p + new Vector3(0.5f, 0f, 0f));
            return Mathf.Max(12f, Mathf.Abs(b.x - a.x) * Surfaces.DisplayScaleX);
        }

        // Every way of leaving a ride funnels through here, so no path can
        // leave _ridePlanet/_floorIndex stale (a stale _ridePlanet used to zero
        // the throw velocity via the rides-off check).
        private void ClearAttachment()
        {
            _attach = null;
            _attachCanvas = null;
            _attachSprite = null;
            _attachFloor = null;
            _attachDeco = null;
            _attachDecoRenderers = null;
            _floorIndex = -1;
            _rideOffset = 0f;
            _ridePlanet = false;
            _rideSlot = -1;
        }

        // Comes back in over a random surface, the way a fresh pet arrives.
        private void RespawnFromTop(float half, float w, float h)
        {
            _lostTime = 0f;
            ClearAttachment();
            if (!Surfaces.TryGetSpawnX(out float x))
                x = Random.Range(half + 20f, w - half - 20f);
            _pos = new Vector2(Mathf.Clamp(x, half, w - half), h + half);
            _vel = Vector2.zero;
            _rotation = 0f;
            _landCooldown = 0f;
            _panicTime = 0f;
            SetState(State.Fall);
        }

        private void DetachFromRide()
        {
            ClearAttachment();
            _landCooldown = 0.6f; // fall clear before grabbing the next thing
            _vel = Vector2.zero;
            SetState(State.Fall); // Fall's tumble logic straightens rotation out
        }

        private void TickCelebrate(float dt, float half, float duration, bool pp)
        {
            _vel.y -= Gravity * dt;
            _pos += _vel * dt;
            if (_pos.y <= half)
            {
                _pos.y = half;
                _vel.y = Random.Range(450f, 650f); // bounce!
                _vel.x = Random.Range(-40f, 40f);
            }

            if (pp)
            {
                _rotation += 540f * dt;
                // through SetColor: dedups same-bucket frames AND keeps
                // _appliedColor honest so the white reset at the end sticks
                SetColor(PlaceholderArt.RainbowAt(_stateTime * 0.8f));
            }

            if (_stateTime > duration)
            {
                _rotation = 0f;
                SetColor(Color.white);
                _vel = Vector2.zero;
                SetState(State.Fall);
            }
        }

        private void TickFaint(float dt, float half)
        {
            // fall to the floor while shocked, then lie flat with X eyes
            if (_pos.y > half)
            {
                _vel.y -= Gravity * dt;
                _pos += _vel * dt;
                if (_pos.y <= half) { _pos.y = half; _vel = Vector2.zero; }
            }
            else
            {
                // packs whose dead art is already lying down (shimeji Sprawl)
                // just settle upright instead of being tipped over
                float flat = _art.Oriented(Pose.Dead) ? 0f : 90f;
                _rotation = Mathf.MoveTowards(_rotation, flat, 400f * dt);
            }

            if (_stateTime > 4f)
            {
                _rotation = 0f;
                ResumeGroundBehavior();
            }
        }

        private void TickDragged(float dt, Vector2 mouse, bool mouseHeld)
        {
            if (dt > 0f)
                _vel = Vector2.Lerp(_vel, (mouse - _lastMouse) / dt, 0.5f);
            _lastMouse = mouse;
            _pos = mouse;
            _rotation = Mathf.Sin(Time.unscaledTime * 10f) * 15f; // dangle wiggle

            if (!mouseHeld)
            {
                _rotation = 0f;
                _vel = Vector2.ClampMagnitude(_vel, ThrowMax); // YEET
                SetState(State.Fall);
            }
        }

        // ---------------- reactions ----------------

        // Force the sprite to be re-fetched next Apply (used after pack swaps:
        // same pose index, different Sprite object behind it).
        public void InvalidateSprite()
        {
            _view?.InvalidateSprite();
        }

        // Pack selection changed: every loaded PackArt was rebuilt, so the old
        // reference is dead either way — roll a fresh pack for this pet.
        public void RerollArt()
        {
            _art = PlaceholderArt.RandomPack() ?? _art;
            InvalidateSprite();
        }

        // Every judgement the player hits shows on this pet's face, in sync
        // with the music: only the hard misses (TooEarly/TooLate/Multipress)
        // draw a reaction, and it is shock. Everything from Perfect down to an
        // extra hit is expected play and passes unremarked. Dead art is
        // reserved for an actual death (see ReactDeath).
        public void ReactJudgement(string marginName)
        {
            switch (marginName)
            {
                case "Perfect":
                case "EarlyPerfect":
                case "LatePerfect":
                case "VeryEarly":
                case "VeryLate":
                case "OverPress": // extra hit
                    return;
                default:
                    SetEmote(Pose.Shock, 0.7f);
                    break;
            }
        }

        // Judgement face-flash: overrides the face briefly without touching
        // the state machine, so strolls and rides continue uninterrupted.
        public void SetEmote(Pose pose, float duration)
        {
            if (_state == State.Faint || _state == State.Dragged) return;
            if (!Allows(PackBehavior.Judgement)) return;
            if (!_art.HasArt(pose)) return; // pack has no art: skip reaction
            _emotePose = pose;
            _emoteTime = duration;
        }

        public void ReactClear(bool purePerfect)
        {
            if (_state == State.Dragged) return;
            if (!Allows(PackBehavior.Celebrate)) return;
            if (!_art.HasArt(Pose.Happy)) return; // no happy art: skip
            _pendingReaction = purePerfect ? State.PPParty : State.Celebrate;
            _hasPendingReaction = true;
            _pendingDelay = Random.Range(0f, 0.45f);
        }

        public void ReactDeath()
        {
            if (_state == State.Dragged) return;
            if (!Allows(PackBehavior.Death)) return;
            if (!_art.HasArt(Pose.Dead)) return; // no dead art: skip
            _pendingReaction = State.Faint;
            _hasPendingReaction = true;
            _pendingDelay = Random.Range(0f, 0.3f);
        }

        private void HandlePendingReaction(float dt)
        {
            if (!_hasPendingReaction) return;
            _pendingDelay -= dt;
            if (_pendingDelay > 0f) return;
            _hasPendingReaction = false;

            if ((_pendingReaction == State.Faint
                    && (!Allows(PackBehavior.Death) || !_art.HasArt(Pose.Dead)))
                || (_pendingReaction != State.Faint
                    && (!Allows(PackBehavior.Celebrate) || !_art.HasArt(Pose.Happy))))
                return;

            ClearAttachment();
            _rotation = 0f;
            SetColor(Color.white);
            if (_pendingReaction == State.Faint)
            {
                _vel = Vector2.zero;
            }
            else
            {
                _vel = new Vector2(Random.Range(-40f, 40f), Random.Range(400f, 600f));
            }
            SetState(_pendingReaction);
        }

        // ---------------- pet-vs-pet collisions (called by the manager) ----------------

        // How this pet participates in the pairwise collision pass.
        public enum CollideKind
        {
            None,    // attached / choreographed: passes through other pets
            Movable, // free body: gets pushed and bounced
            Static,  // dragged or fainted: an immovable wall others bounce off
        }

        public CollideKind Collidability()
        {
            switch (_state)
            {
                case State.Fall:
                case State.Walk:
                case State.Sit:
                case State.Idle:
                case State.Stand:
                case State.Celebrate:
                case State.PPParty:
                // Riders and wall crawlers are steered by their surface rather
                // than by _pos, but they still occupy space: separation for
                // these goes through the surface parameter (see below).
                case State.Climb:
                case State.Ceiling:
                    return CollideKind.Movable;
                case State.Dragged:
                    return CollideKind.Static;
                // Faint: a dead pet is a ghost — no hitbox, nothing bounces
                // off it, nothing can shove it around
                default: // Faint/TileLeap
                    return CollideKind.None;
            }
        }

        // Which collision group this pet belongs to right now: riding a tile /
        // decoration / planet, or moving in screen space (floor, walls,
        // ceiling, mid-air). Each group has its own toggle — see
        // Surfaces.PetCollides.
        public bool CollidesAsRider => Riding;

        // circle a bit tighter than the sprite box, so pets visually touch
        public static float SharedRadius => _sharedSize * 0.5f * 0.85f;
        public float Radius => SharedRadius;

        // How fast this pet is walking around a planet / along a tile, in px/s.
        private float RideSpeed => WalkSpeed * 0.8f * PanicMul;

        // velocity for collision math — most states move by something other
        // than _vel, and the closing-speed test needs the real thing
        public Vector2 CollisionVel
        {
            get
            {
                switch (_state)
                {
                    case State.Walk:
                        if (!Riding) return new Vector2(_dir * WalkSpeed, 0f);
                        // planet lap: _dir = 1 decreases the angle, so the
                        // screen-space heading is the tangent at _rideAngle
                        if (_ridePlanet)
                            return new Vector2(Mathf.Sin(_rideAngle), -Mathf.Cos(_rideAngle))
                                   * (_dir * RideSpeed);
                        return new Vector2(_dir * RideSpeed, 0f);
                    case State.Climb: return new Vector2(0f, ClimbSpeed);
                    case State.Ceiling: return Vector2.zero; // hangs in place
                    // a pet sat down or standing around isn't going anywhere,
                    // on a ride or otherwise (_vel is zeroed on landing)
                    default: return _vel;
                }
            }
        }

        // Positional de-overlap; next Tick re-clamps to the screen.
        // A rider's _pos is recomputed from its ride every frame, so moving
        // _pos alone is erased instantly — the push has to land on the ride
        // parameter (offset along the surface / angle around the planet).
        public void CollisionSeparate(Vector2 delta)
        {
            if (Riding)
            {
                if (_ridePlanet)
                {
                    float r = Mathf.Max(RideSurfaceHalfWidth() + Size() * 0.5f * 0.8f, 1f);
                    // tangential component of the push, converted to radians
                    _rideAngle += (delta.y * Mathf.Cos(_rideAngle)
                                 - delta.x * Mathf.Sin(_rideAngle)) / r;
                }
                else _rideOffset += delta.x;
            }
            _pos += delta;
        }

        // gentle bump: a walker turns to walk away from the other pet — or
        // stops to wave at it, if the pack draws a wave and this pet isn't
        // still on cooldown from the last one.
        public void CollisionTurnAway(float awayX)
        {
            // stacked near-vertically (two tiers of tiles): there is no
            // sideways escape, and flipping every frame just dithers
            if (Mathf.Abs(awayX) < 0.3f) return;
            int away = awayX > 0f ? 1 : -1;
            switch (_state)
            {
                case State.Walk:
                    // on a planet, which _dir walks "left" depends on where
                    // around the circle the pet is standing
                    int outward = Riding && _ridePlanet
                        ? (away * Mathf.Sin(_rideAngle) > 0f ? 1 : -1)
                        : away;
                    // greeting faces the other pet, i.e. the way it wasn't
                    // about to walk. Waving leaves State.Walk, so this stops
                    // firing while the wave plays out.
                    if (StartGreet(-outward)) break;
                    _dir = outward;
                    break;
            }
        }

        // hard bump: take the impulse and go ballistic
        public void CollisionBounce(Vector2 newVel)
        {
            if (Collidability() != CollideKind.Movable) return;
            // a hard enough hit knocks a rider or wall crawler clean off
            if (Riding || _state == State.Climb || _state == State.Ceiling)
            {
                ClearAttachment();
                _landCooldown = 0.6f; // fall clear before grabbing the next thing
                _rotation = 0f;
            }
            _vel = newVel;
            _bonkTime = Allows(PackBehavior.Bonk) ? 0.6f : 0f;
            // celebrations keep celebrating (their tick integrates _vel too)
            if (_state != State.Fall && _state != State.Celebrate && _state != State.PPParty)
                SetState(State.Fall);
        }

        // ---------------- input (called by the manager) ----------------

        public Vector2 Pos => _pos;
        public bool IsDragged => _state == State.Dragged;

        // dragged: already held; fainted: dead pets have no hitbox and
        // can't be picked up — let them rest in peace
        public bool Grabbable => _state != State.Dragged && _state != State.Faint;

        // half-extent of the square the mouse picks a pet up by
        public static float GrabHalf => _sharedSize * 0.5f;

        public bool Contains(Vector2 p)
        {
            if (!Grabbable) return false;
            float half = GrabHalf;
            return Mathf.Abs(p.x - _pos.x) < half && Mathf.Abs(p.y - _pos.y) < half;
        }

        // Dropped on the spot, without a throw: the mouse was taken away (a
        // render captures the pets) rather than released.
        public void LetGo()
        {
            if (_state != State.Dragged) return;
            _vel = Vector2.zero;
            _rotation = 0f;
            SetState(State.Fall);
        }

        // The space pets live in changed size: keep this pet at the same place
        // relative to the screen. Riders follow their ride; the walking offset
        // along it scales with the camera's pixels per unit, i.e. the height.
        public void RescaleSpace(float sx, float sy)
        {
            _pos = new Vector2(_pos.x * sx, _pos.y * sy);
            _vel = new Vector2(_vel.x * sx, _vel.y * sy);
            _rideOffset *= sy;
        }

        public void StartDrag(Vector2 mouse)
        {
            ClearAttachment(); // yanked off whatever it was holding
            _lastMouse = mouse;
            _vel = Vector2.zero;
            _rotation = 0f;
            SetColor(Color.white);
            SetState(State.Dragged);
        }

        // ---------------- presentation ----------------

        private void SetState(State next)
        {
            _state = next;
            _aiming = false;
            _edgeTime = 0f;
            _stateTime = 0f;
            _animClock = 0f;
        }

        private void SetColor(Color c)
        {
            _view.SetColor(c);
        }

        private void Apply(float half)
        {
            // whole-pixel quantize: zoom animations sweep the scale smoothly,
            // and un-quantized that means a sizeDelta layout dirty for every
            // pet on every frame of the sweep. Sub-pixel size is invisible.
            float size = Mathf.Round(half * 2f);
            // panic (imminent doom) outranks judgement flashes; both defer to
            // faint/drag, whose faces are part of the choreography
            bool faceFree = _state != State.Faint && _state != State.Dragged;
            Pose pose = Allows(PackBehavior.Panic) && _panicTime > 0f
                && faceFree && _art.HasArt(Pose.Panic)
                ? Pose.Panic
                : _emoteTime > 0f && faceFree ? _emotePose : CurrentPose();

            // face direction of travel. Which mirror means "forward" depends on
            // the art: our own faces right, shimeji art faces left, so the flip
            // is taken relative to the pose's own facing.
            float facing = _art.Facing(pose);
            float flip = _dir < 0 ? -facing : facing;
            if (_state == State.Climb || _state == State.Ceiling || _state == State.Faint) flip = 1f;
            // dedicated wall art grips the wall it's drawn against: mirror it
            // for the other side — which wall that is again follows the art's
            // facing. Ceiling art stays unmirrored — it's drawn rotated, so
            // flipping x would swap which way is "up".
            if (_state == State.Climb && _art.Oriented(Pose.Climb))
                flip = (_climbSide < 0 ? -1f : 1f) * _art.Facing(Pose.Climb);
            _view.ApplyLayout(_pos, _rotation, size, flip);

            int count = _art.FrameCount(pose);
            float fps = pose == Pose.Happy ? 6f : 8f;
            int frame = 0;
            if (_state != State.Stand && count > 1)
            {
                int tick = (int)(_animClock * fps);
                if (pose == Pose.Walk && _art.PingPongWalk && count > 2)
                {
                    // 0 1 2 3 4 3 2 1 | 0 1 2 ... — endpoints not doubled
                    int cycle = count * 2 - 2;
                    int k = tick % cycle;
                    frame = k < count ? k : cycle - k;
                }
                else frame = tick % count;
            }
            int key = ((int)pose << 8) | frame;
            _view.ApplySprite(key, _art.Get(pose, frame));
        }

        private Pose CurrentPose()
        {
            switch (_state)
            {
                case State.Walk: return _edgeTime > 0f ? Pose.Edge : Pose.Walk;
                // Climb/Ceiling borrow Walk unless the pack draws them (shimeji
                // packs do — shime12-14 on the wall, shime23-25 on the ceiling)
                case State.Climb: return Pose.Climb;
                case State.Ceiling: return Pose.Ceiling;
                case State.Sit: return Pose.Sit;
                case State.Idle: return _waving ? Pose.Wave : Pose.Idle;
                case State.Stand: return Pose.Walk;
                // fast fall (thrown, evicted, long drop) = scared plummet face;
                // slow drift keeps the calm fall art
                case State.Fall: return Allows(PackBehavior.Bonk)
                    && _bonkTime > 0f && _art.HasArt(Pose.Bonk)
                    ? Pose.Bonk
                    : _vel.sqrMagnitude > TumbleSpeed * TumbleSpeed ? Pose.Falling : Pose.Fall;
                case State.TileLeap: return Pose.Jump;
                case State.Dragged: return Pose.Fall;
                case State.Celebrate:
                case State.PPParty: return Pose.Happy;
                // ReactDeath only fires with dead art present; shock may
                // still be missing on its own
                case State.Faint: return _stateTime < 0.8f && _art.HasArt(Pose.Shock)
                    ? Pose.Shock : Pose.Dead;
                default: return Pose.Walk;
            }
        }
    }
}
