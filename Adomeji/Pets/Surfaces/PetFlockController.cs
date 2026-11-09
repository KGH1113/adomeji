using System.Collections.Generic;
using Adomeji.Gameplay.Patches;
using Adomeji.Composition;
using Adomeji.Pets.Actors;
using Adomeji.Pets.Physics;
using Adomeji.Sprites.Loading;
using UnityEngine;
using UnityEngine.UI;

namespace Adomeji.Pets.Surfaces
{
    // Persistent overlay that owns all shimeji instances. Survives scene loads.
    //
    // The whole flock is driven from this single Update(): one MonoBehaviour
    // callback instead of N (each native->managed Update call has overhead),
    // and per-frame data (dt, screen size, mouse) is read once and passed down.
    // Optional Optimized Mode ticks the simulation at 30 Hz.
    public class PetFlockController : MonoBehaviour
    {
        public static PetFlockController Instance { get; private set; }

        private Canvas _canvas;
        private readonly List<PetActor> _pets = new List<PetActor>();
        // this frame's living pets, compacted once (see Update)
        private PetActor[] _live = new PetActor[0];
        private int _liveCount;
        private int _appliedCount = -1;
        private float _accumulator;
        private bool _mouseDownLatch; // survives frames skipped by Optimized Mode
        private bool _overlayOn;      // hitbox overlay component attached?

        // --- no-floor mode: cache of tiles currently near the camera ---
        // Two tiers: a cheap world-rect filter every 0.25s picks candidates
        // (no projections), then each frame only the candidates are projected
        // to screen space for the pets to land on.
        private float _floorScanTimer;

        // One scanned tile. Transform, GameObject, Renderer and FloorMesh
        // lookups are native calls and the outline costs a shape-key string
        // plus a dictionary probe — none of which change while a tile sits
        // there, so all of it is resolved once and held across rescans (see
        // _floorMemo) instead of being re-derived for 256 tiles four times a
        // second.
        private class FloorCand
        {
            public scrFloor Floor;
            public Transform T;
            public GameObject Go;
            public Renderer Rend;
            public Transform RendT;
            public FloorMesh Mesh;   // null on sprite / raw-mesh tiles
            public string HullKey;   // shape key the held outline was built from
            public Vector2[] Hull;
            public int PathIndex;    // listFloors index; < 0 = decoration tile
            // non-null when the "tile" is really a FLOOR OBJECT DECORATION:
            // those carry a genuine scrFloor (scrObjectDecoration.floor), so
            // they ride the tile path and get the exact same outline, surface
            // and walking — only their visibility comes from the decoration.
            public scrDecoration Deco;
        }

        private readonly List<FloorCand> _floorCands = new List<FloorCand>();
        private readonly Dictionary<scrFloor, FloorCand> _floorMemo =
            new Dictionary<scrFloor, FloorCand>();
        private Object _lastLevelMaker; // memo is dropped when the level changes

        // the tile AS RENDERED. FloorMesh bakes the slab's angle into the mesh
        // VERTICES (GetPositions rotates in local space; the transform stays
        // unrotated), so local bounds are axis-aligned lies — the only honest
        // shape is the mesh itself. Rescan takes the convex hull of the mesh
        // vertices (sprite tiles: the sprite's corner quad, which DOES rotate
        // via the transform); each frame the hull is projected to screen.
        // Landing, walking and the overlay all read the projected hull; its
        // top envelope is the walkable surface.
        private const int MaxHullPts = 32;
        private static readonly float[] FHx = new float[256 * MaxHullPts]; // hull, screen px
        private static readonly float[] FHy = new float[256 * MaxHullPts];
        private static readonly int[] FHn = new int[256]; // points this frame; 0 = no hull
        private static readonly float[] Fx = new float[256];
        private static readonly float[] Fy = new float[256];
        // real surface geometry, from the tile's own renderer bounds: a tile is
        // 0.75 x 0.4125 world units (0.5 long for short tiles), NOT the 1-unit
        // box the node spacing suggests — assuming the bigger box put pets a
        // third of a tile out over open air and hovering above the surface.
        private static readonly float[] FHalf = new float[256];   // half width, px
        private static readonly float[] FTopOff = new float[256]; // node y -> surface top, px
        private static readonly float[] FCxOff = new float[256];  // node x -> slab center, px
        private static readonly Transform[] Ft = new Transform[256];
        private static readonly scrFloor[] Ff = new scrFloor[256];
        private static readonly int[] Fidx = new int[256];
        // tile screen movement since last frame (px), matched by transform: the
        // landing sweep must run in the tile's reference frame or a scrolling
        // camera moves the tile further per frame than the pet falls and the
        // pet tunnels straight through the slab
        private static readonly float[] FDx = new float[256];
        private static readonly float[] FDy = new float[256];
        private static readonly Dictionary<Transform, Vector2> _floorPrevPos =
            new Dictionary<Transform, Vector2>(256);
        private static int _floorCount;
        private static float _floorHalfWidthPx;
        // listFloors index -> this frame's cache slot. Riders hit the cache
        // several times a frame (surface height, metrics, neighbors); at 100
        // pets a linear scan over 256 tiles per hit is the whole frame budget.
        private static readonly Dictionary<int, int> _floorSlots = new Dictionary<int, int>(256);

        // fallbacks when a tile has no reachable renderer (world units -> px
        // via _floorHalfWidthPx, which is half a world unit)
        private const float TileHalfLenUnits = 0.375f;  // LongDimensions.x * 0.5
        private const float TileHalfThickUnits = 0.21f; // LongDimensions.y * 0.5

        // level-editor decorations are walkable too: any visible sprite
        // decoration acts as a one-off platform (pet stands on the sprite
        // bounds' top edge). Pivot placement is arbitrary in the editor, so
        // everything is measured from the rendered bounds, not the transform.
        private const int MaxDecos = 128;
        // an object decoration's visual is COMPOSED of several renderers (a
        // floor object is tile top + borders + icon, and the planet variant is
        // a PlanetRenderer, not a SpriteRenderer at all) — so each candidate
        // carries its full renderer set and the surface is their combined bounds
        private readonly List<Renderer[]> _decoCandidates = new List<Renderer[]>();
        // per-decoration lookups resolved once and held across rescans (same
        // deal as _floorMemo); dropped when the level changes
        private class DecoCand
        {
            public Transform T;
            public GameObject Go;
            public Renderer[] Rends; // resolved lazily: only floor-ish props need it
        }

        private readonly Dictionary<scrObjectDecoration, DecoCand> _decoMemo =
            new Dictionary<scrObjectDecoration, DecoCand>();
        // paired 1:1 with _decoCandidates: fades go through the decoration's
        // own opacity (custom shader), NOT renderer color — so the
        // scrDecoration itself must be consulted for visibility
        private readonly List<scrDecoration> _decoCandidateDecos = new List<scrDecoration>();
        private static readonly float[] Dx = new float[MaxDecos];    // bounds-center screen x
        private static readonly float[] DTop = new float[MaxDecos];  // bounds-top screen y
        private static readonly float[] DHalf = new float[MaxDecos]; // half width, px
        private static readonly Transform[] Dt = new Transform[MaxDecos];
        // top-edge movement since last frame (px) — same tunneling fix as FDy
        private static readonly float[] DDy = new float[MaxDecos];
        private static readonly Dictionary<Transform, float> _decoPrevTop =
            new Dictionary<Transform, float>(MaxDecos);
        private static int _decoCount;

        // planets are landing surfaces too (round ones). Game versions differ
        // on which transform actually carries the visible ball (scrPlanet
        // itself vs its PlanetRenderer child), so we track BOTH per planet —
        // whichever one moves is the one that hits things. Entries remember
        // their scrPlanet root so a pet riding any part of a planet can't be
        // bonked by that same planet's other entry.
        // ADOFAI keeps 8 scrPlanet objects alive at all times (red, blue plus
        // six spares for multi-planet levels), so the cache has to fit every
        // one of them times two transforms each.
        private const int MaxPlanets = 20;
        // fraction of the projected ball radius that counts as solid
        private const float PlanetRadiusKeep = 0.9f;
        private readonly List<Transform> _planetBalls = new List<Transform>();
        private readonly List<Transform> _planetRoots = new List<Transform>();
        // non-null for planet OBJECT DECORATIONS riding in this cache: their
        // visibility must be re-checked against the decoration's opacity
        private readonly List<scrDecoration> _planetBallDecos = new List<scrDecoration>();
        private static readonly float[] Plx = new float[MaxPlanets];
        private static readonly float[] Ply = new float[MaxPlanets];
        private static readonly float[] PlR = new float[MaxPlanets]; // ball radius, px
        private static readonly Transform[] Plt = new Transform[MaxPlanets];
        private static readonly Transform[] PlRootT = new Transform[MaxPlanets];
        private static int _planetCount;

        // planets fast enough to count as a hit/threat this frame. Parked
        // planets are furniture, and every pet used to re-test all of them
        // (including a native IsChildOf) twice per frame.
        private static readonly int[] PlMover = new int[MaxPlanets];
        private static int _planetMoverCount;
        public static bool AnyPlanetMoving => _planetMoverCount > 0;

        // planet screen velocity (px/s), matched frame-to-frame by transform —
        // a planet swinging through a pet is a legitimate traffic accident
        private static readonly float[] PlVx = new float[MaxPlanets];
        private static readonly float[] PlVy = new float[MaxPlanets];
        private static readonly float[] PlSegX = new float[MaxPlanets]; // last frame's position:
        private static readonly float[] PlSegY = new float[MaxPlanets]; // swept collision, no tunneling
        private static readonly float[] PlPrevX = new float[MaxPlanets];
        private static readonly float[] PlPrevY = new float[MaxPlanets];
        private static readonly Transform[] PlPrevT = new Transform[MaxPlanets];
        private static int _planetPrevCount;

        // how zoomed-in the game camera is, 1 = typical; pets scale with it
        private static float _zoomFactor = 1f;
        public static float ZoomFactor => _zoomFactor;

        // ---- ScreenTile display mapping ----
        // The ScreenTile filter event repeats the rendered frame tileX x tileY
        // times across the screen (shader: source sampled at frac(uv * tile)),
        // so where the camera THINKS a tile is and where the player SEES it
        // diverge. Every projection is remapped into the display cell that
        // contains the object's anchor — one object's points all use the same
        // cell, so shapes never straddle two copies.
        private ScreenTile _screenTileFx;
        private static float _dispTileX = 1f, _dispTileY = 1f;
        // false = no ScreenTile effect, so remapping is the identity and the
        // per-point calls can be skipped outright
        private static bool _displayTiled;

        // read once per frame: Screen.width/height and the camera lookup are
        // native calls, and the projection path touches them per point
        private static float _scrW = 1f, _scrH = 1f;
        private static Camera _cam;

        // Batched world->screen projection. Camera.WorldToScreenPoint and
        // Transform.TransformPoint are native calls; projecting every tile's
        // outline through them is thousands of interop crossings per frame.
        // Same math, done in managed code off matrices fetched once.
        private static Matrix4x4 _viewM, _projM;
        // world -> clip in one step, for the per-point hot path below
        private static Matrix4x4 _vpM;
        private static float _vpX, _vpY, _vpW, _vpH;

        private static void SetupProjection(Camera cam)
        {
            _viewM = cam.worldToCameraMatrix;
            _projM = cam.projectionMatrix;
            _vpM = _projM * _viewM;
            Rect r = cam.pixelRect;
            _vpX = r.x; _vpY = r.y; _vpW = r.width; _vpH = r.height;
        }

        // View-space point -> (screen x, screen y, distance in front of camera),
        // matching Camera.WorldToScreenPoint's conventions.
        private static Vector3 ProjectView(Vector3 v)
        {
            Vector3 ndc = _projM.MultiplyPoint(v); // perspective divide included
            return new Vector3(_vpX + (ndc.x * 0.5f + 0.5f) * _vpW,
                _vpY + (ndc.y * 0.5f + 0.5f) * _vpH, -v.z);
        }

        private static Vector3 Project(Vector3 world) =>
            ProjectView(_viewM.MultiplyPoint3x4(world));

        public static float DisplayScaleX => 1f / _dispTileX;
        public static float DisplayScaleY => 1f / _dispTileY;

        public static float DisplayMapX(float x, float anchorX)
        {
            if (_dispTileX > 0.9999f && _dispTileX < 1.0001f) return x;
            float w = _scrW;
            float k = Mathf.Floor(Mathf.Clamp01(anchorX / w) * _dispTileX);
            k = Mathf.Min(k, Mathf.Max(0f, Mathf.Ceil(_dispTileX) - 1f));
            return (x + k * w) / _dispTileX;
        }

        public static float DisplayMapY(float y, float anchorY)
        {
            if (_dispTileY > 0.9999f && _dispTileY < 1.0001f) return y;
            float h = _scrH;
            float k = Mathf.Floor(Mathf.Clamp01(anchorY / h) * _dispTileY);
            k = Mathf.Min(k, Mathf.Max(0f, Mathf.Ceil(_dispTileY) - 1f));
            return (y + k * h) / _dispTileY;
        }

        // ---- screen filters on pets ----
        // ADOFAI's filter events are CameraFilterPack image effects living on
        // the gameplay camera's own GameObject. A ScreenSpaceOverlay canvas
        // renders after every camera, so pets normally float above the
        // filters, unfiltered. With FilterPets on, the canvas is re-homed as
        // ScreenSpaceCamera on that camera: pets are drawn inside its render
        // (before OnRenderImage), and grayscale/VHS/blur/tiling all apply to
        // them exactly as they do to the level. Menus (no gameplay camera)
        // and the setting being off fall back to the overlay.
        private bool _canvasOnCamera;

        private void ApplyCanvasMode(Camera cam)
        {
            bool want = RuntimeContext.Settings.FilterPets && cam != null;
            if (want)
            {
                if (_canvas.worldCamera != cam) _canvas.worldCamera = cam;
                if (_canvas.renderMode != RenderMode.ScreenSpaceCamera)
                {
                    _canvas.renderMode = RenderMode.ScreenSpaceCamera;
                    // Inside the camera, draw order is decided by SORTING
                    // LAYER first (Bg < Default < Floor < FloorCover <
                    // FloorTop < FgFlash), order-in-layer second — a Default
                    // canvas would sit UNDER every tile no matter its 30000.
                    // FgFlash is the layer the foreground Flash event draws
                    // on (at order 0), so FgFlash at a deeply negative order
                    // = pets above the whole level, still covered by
                    // foreground flashes. Unknown layer name = Unity keeps
                    // Default, which just reverts to pets-under-tiles.
                    _canvas.sortingLayerName = "FgFlash";
                    _canvas.sortingOrder = -30000;
                }
                _canvas.planeDistance = cam.nearClipPlane + 1f;
            }
            else if (_canvas.renderMode != RenderMode.ScreenSpaceOverlay)
            {
                _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                _canvas.worldCamera = null;
                _canvas.sortingLayerID = 0; // Default; overlay ignores layers
                _canvas.sortingOrder = 30000;
            }
            _canvasOnCamera = want;
        }

        // ---- TUFReplay video capture ----
        // While a TUFReplay render draws the pet canvas into its video, the
        // renderer owns the canvas: it re-homes it on its own HUD camera, after
        // the level's filters, and restores it afterwards. Pets then live in the
        // video's pixel space rather than the window's, and the mouse can't
        // reach them.
        private bool _videoCapture;
        private Vector2 _videoSize;

        // The pixel space pets live in: the game window, or the video while a
        // render captures the pets.
        public static float SpaceWidth =>
            Instance != null && Instance._videoCapture ? Instance._videoSize.x : Screen.width;
        public static float SpaceHeight =>
            Instance != null && Instance._videoCapture ? Instance._videoSize.y : Screen.height;

        public static Canvas BeginVideoCapture(int width, int height)
        {
            var flock = Instance;
            if (flock == null) return null;

            var window = new Vector2(Screen.width, Screen.height);
            var video = new Vector2(width > 0 ? width : window.x, height > 0 ? height : window.y);
            if (!flock._videoCapture)
            {
                foreach (var pet in flock._pets)
                    if (pet != null) pet.LetGo();
                flock.RescalePets(window, video);
            }
            else flock.RescalePets(flock._videoSize, video);

            flock._videoCapture = true;
            flock._videoSize = video;
            flock._canvasOnCamera = false;
            flock._mouseDownLatch = false;
            GameplayPatchBridge.SuppressGameMouse = false;
            return flock._canvas;
        }

        public static void EndVideoCapture()
        {
            var flock = Instance;
            if (flock == null || !flock._videoCapture) return;
            flock._videoCapture = false;
            flock.RescalePets(flock._videoSize, new Vector2(Screen.width, Screen.height));
        }

        // Keep every pet at the same place relative to the screen when the
        // space changes size, instead of pinning it to the new edge.
        private void RescalePets(Vector2 from, Vector2 to)
        {
            if (from.x <= 0f || from.y <= 0f || to.x <= 0f || to.y <= 0f || from == to) return;
            float sx = to.x / from.x, sy = to.y / from.y;
            foreach (var pet in _pets)
                if (pet != null) pet.RescaleSpace(sx, sy);
        }

        // true only when the mode is on AND there are tiles to land on;
        // menus and empty scenes quietly behave like normal mode
        public static bool NoFloorActive { get; private set; }
        // walls/ceiling need no tiles to fall back on — the floor still catches
        // pets — but menus (no camera) still leave every surface solid
        public static bool NoWallActive { get; private set; }
        public static bool NoCeilingActive { get; private set; }

        public static PetFlockController Create()
        {
            if (Instance != null) return Instance;
            var go = new GameObject("Adomeji");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<PetFlockController>();
            return Instance;
        }

        public static void DestroyAll()
        {
            if (Instance == null) return;
            Destroy(Instance.gameObject);
            Instance = null;
        }

        public static void NotifyClear(bool purePerfect)
        {
            if (Instance == null) return;
            foreach (var pet in Instance._pets)
                if (pet != null) pet.ReactClear(purePerfect);
        }

        public static void NotifyDeath()
        {
            if (Instance == null) return;
            foreach (var pet in Instance._pets)
                if (pet != null) pet.ReactDeath();
        }

        // Every judgement the player hits shows on every pet's face, in sync
        // with the music. Pose choice is per pet — each pet's pack has its
        // own art (see PetActor.ReactJudgement).
        public static void NotifyJudgement(string marginName)
        {
            if (Instance == null) return;
            foreach (var pet in Instance._pets)
                if (pet != null) pet.ReactJudgement(marginName);
        }

        private void Awake()
        {
            var canvasGo = new GameObject("ShimejiCanvas");
            canvasGo.transform.SetParent(transform, false);
            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 30000;
            // pets are plain textured quads: no normals/tangents/uv1 in the
            // vertex stream, which is pure upload cost at 100 of them
            _canvas.additionalShaderChannels = AdditionalCanvasShaderChannels.None;

            PlaceholderArt.ApplyPacks(RuntimeContext.Settings.SpritePacks);
        }

        // Apply the configured pack selection right now (settings UI calls
        // this on every pack toggle / role edit — the only places the
        // selection changes). Every pet re-rolls which pack it wears, so the
        // random spread always reflects the current selection.
        public static void ApplyPacksNow()
        {
            PlaceholderArt.ApplyPacks(RuntimeContext.Settings.SpritePacks);
            RerollAll();
        }

        // Re-roll which pack every pet wears, without reloading any art — the
        // Respawn button, so a freshly dragged spawn mix shows up on the pets
        // already on screen instead of only on the next spawn.
        public static void RerollAll()
        {
            if (Instance == null) return;
            foreach (var pet in Instance._pets)
                if (pet != null) pet.RerollArt();
        }

        public void Tick(float frameDeltaTime)
        {
            int want = Mathf.Clamp(RuntimeContext.Settings.Count, 1, RuntimeContext.Settings.MaxPets);
            if (want != _appliedCount)
            {
                _appliedCount = want;
                ResizeFlock(want);
            }

            // the hitbox overlay is a developer toggle, and an OnGUI method is
            // billed every frame it exists (layout + repaint + one pass per
            // input event) whether or not it draws — so it lives on a separate
            // component that only exists while the toggle is on
            if (RuntimeContext.Settings.ShowHitboxes != _overlayOn)
            {
                _overlayOn = RuntimeContext.Settings.ShowHitboxes;
                if (_overlayOn) gameObject.AddComponent<HitboxOverlay>();
                else { var o = GetComponent<HitboxOverlay>(); if (o != null) Destroy(o); }
            }

            // the cursor lives in the window, not in a render's video
            bool pointer = !_videoCapture;
            if (pointer) _mouseDownLatch |= Input.GetMouseButtonDown(0);

            float dt = Mathf.Min(frameDeltaTime, 0.05f);
            if (RuntimeContext.Settings.OptimizedMode)
            {
                _accumulator += dt;
                if (_accumulator < 1f / 30f) return; // this frame is a gift to the CPU
                dt = Mathf.Min(_accumulator, 0.05f);
                _accumulator = 0f;
            }
            dt *= RuntimeContext.Settings.Speed;

            UpdateSurfaceCache(dt);
            PetActor.RefreshSharedSize(); // same for every pet; depends on the zoom just computed

            // Compact the flock once per frame. `pet == null` on a Unity object
            // is a native liveness call, and the tick, grab, collision and
            // planet-capacity passes would otherwise each pay for it per pet.
            if (_live.Length < _pets.Count) _live = new PetActor[_pets.Count];
            int live = 0;
            for (int i = 0; i < _pets.Count; i++)
            {
                var pet = _pets[i];
                if (pet != null) _live[live++] = pet;
            }
            _liveCount = live;

            // read shared frame data once, not once per pet
            float w = _scrW;
            float h = _scrH;
            Vector2 mouse = pointer ? (Vector2)Input.mousePosition : Vector2.zero;
            bool mouseHeld = pointer && Input.GetMouseButton(0);
            bool mouseDown = _mouseDownLatch;
            _mouseDownLatch = false;

            // grab arbitration: one click grabs exactly one pet — the one
            // whose center is nearest the cursor, even when several overlap
            if (mouseDown && RuntimeContext.Settings.AllowDrag)
            {
                PetActor best = null;
                float bestDist = float.MaxValue;
                for (int i = 0; i < live; i++)
                {
                    var pet = _live[i];
                    if (!pet.Contains(mouse)) continue;
                    float d = (pet.Pos - mouse).sqrMagnitude;
                    if (d < bestDist) { bestDist = d; best = pet; }
                }
                if (best != null) best.StartDrag(mouse);
            }

            bool anyDragged = false;
            for (int i = 0; i < live; i++)
            {
                var pet = _live[i];
                pet.Tick(dt, w, h, mouse, mouseHeld);
                anyDragged |= pet.IsDragged;
            }

            // holding a pet swallows the left button for the rest of the game
            GameplayPatchBridge.SuppressGameMouse = anyDragged && RuntimeContext.Settings.SwallowMouse;

            if ((RuntimeContext.Settings.PetCollisionsTiles || RuntimeContext.Settings.PetCollisionsScreen)
                && live > 1) ResolvePetCollisions();
        }

        // ---------------- pet-vs-pet collisions ----------------
        // Circle hitboxes, one pairwise pass per frame (n ≤ 8, so at most 28
        // pairs — cheaper than a physics engine by a comedy margin).
        //
        // Two regimes split by closing speed: a slow bump just de-overlaps and
        // turns walkers around; a fast hit is an elastic bounce that sends both
        // flying. Dragged pets act as immovable walls — waving a held pet
        // through the flock is a bowling ball. Riders and wall crawlers take
        // part too: their push is applied in ride space (see CollisionSeparate),
        // since their _pos is regenerated from the surface every frame.
        private const float HardBumpSpeed = 260f;   // closing faster than this = real bounce
        private const float PetRestitution = 0.7f;  // energy kept in a pet-pet bounce

        // Each pet answers to the toggle for wherever it currently is. Applied
        // per pet, so two riders follow the tile switch, two pets on the screen
        // floor/walls/ceiling follow the screen switch, and a mixed pair needs
        // both switches on.
        private static bool PetCollides(PetActor p) => p.CollidesAsRider
            ? RuntimeContext.Settings.PetCollisionsTiles
            : RuntimeContext.Settings.PetCollisionsScreen;

        // sweep-and-prune scratch: pets sorted by screen x so the inner loop
        // can stop at the first pet too far right to touch. All-pairs is 4950
        // tests at 100 pets; sorted, it's a handful of neighbors each.
        private int[] _colIdx = new int[0];
        private float[] _colKey = new float[0];
        // collidability of each entry, decided in the build pass — the inner
        // loop visits a pet once per neighbour, and re-deriving it there means
        // paying for the state switch and the settings lookup per PAIR
        private bool[] _colMoves = new bool[0];

        private void ResolvePetCollisions()
        {
            if (_colIdx.Length < _liveCount)
            {
                _colIdx = new int[_liveCount];
                _colKey = new float[_liveCount];
                _colMoves = new bool[_liveCount];
            }

            int cnt = 0;
            for (int i = 0; i < _liveCount; i++)
            {
                var p = _live[i];
                if (p.Collidability() == PetActor.CollideKind.None || !PetCollides(p)) continue;
                _colKey[cnt] = p.Pos.x;
                _colIdx[cnt] = i;
                cnt++;
            }
            if (cnt < 2) return;
            System.Array.Sort(_colKey, _colIdx, 0, cnt);
            // the sort moved the entries, so the movable flags are filled in
            // afterwards, against their final positions
            for (int i = 0; i < cnt; i++)
                _colMoves[i] = _live[_colIdx[i]].Collidability() == PetActor.CollideKind.Movable;

            // every pet is the same size, so one reach covers all pairs
            float reach = PetActor.SharedRadius * 2f;
            float reachSq = reach * reach;

            for (int ii = 0; ii < cnt; ii++)
            {
                var a = _live[_colIdx[ii]];
                bool aMoves = _colMoves[ii];

                for (int jj = ii + 1; jj < cnt; jj++)
                {
                    if (_colKey[jj] - _colKey[ii] > reach) break; // and so is every pet after it
                    bool bMoves = _colMoves[jj];
                    if (!aMoves && !bMoves) continue; // two walls don't collide
                    var b = _live[_colIdx[jj]];

                    Vector2 d = a.Pos - b.Pos;
                    if (d.sqrMagnitude >= reachSq) continue;

                    float dist = d.magnitude;
                    Vector2 n = dist > 0.01f ? d / dist : Vector2.up; // b -> a
                    float overlap = reach - dist;

                    // de-overlap: split the push, or shove only the movable one
                    if (aMoves && bMoves)
                    {
                        a.CollisionSeparate(n * (overlap * 0.5f));
                        b.CollisionSeparate(n * (-overlap * 0.5f));
                    }
                    else if (aMoves) a.CollisionSeparate(n * overlap);
                    else b.CollisionSeparate(n * -overlap);

                    float vn = Vector2.Dot(a.CollisionVel - b.CollisionVel, n);
                    if (vn >= 0f) continue; // already separating

                    if (-vn > HardBumpSpeed)
                    {
                        // elastic bounce (equal masses; walls reflect the full
                        // relative velocity, so a swung held pet transfers its speed)
                        if (aMoves && bMoves)
                        {
                            float jm = -(1f + PetRestitution) * vn * 0.5f;
                            a.CollisionBounce(a.CollisionVel + n * jm);
                            b.CollisionBounce(b.CollisionVel - n * jm);
                        }
                        else if (aMoves)
                        {
                            a.CollisionBounce(a.CollisionVel - n * ((1f + PetRestitution) * vn));
                        }
                        else
                        {
                            b.CollisionBounce(b.CollisionVel + n * ((1f + PetRestitution) * vn));
                        }
                    }
                    else
                    {
                        // polite bump: walkers about-face and stroll apart
                        a.CollisionTurnAway(n.x);
                        b.CollisionTurnAway(-n.x);
                    }
                }
            }
        }

        private void OnDestroy()
        {
            GameplayPatchBridge.SuppressGameMouse = false;
            NoFloorActive = NoWallActive = NoCeilingActive = false;
            PlaceholderArt.UnloadAll();
        }

        // ---------------- developer hitbox overlay ----------------
        // Draws exactly what the pets can land on: the same cached, projected
        // surfaces the landing queries read — not the raw game objects. If a
        // surface doesn't show up here, pets can't see it either.
        //
        // Lives on its own component, attached only while the toggle is on:
        // Unity bills every OnGUI method once per layout, once per repaint and
        // once per input event, and an early `return` inside it is billed all
        // the same. The nested class reads the manager's private surface cache
        // directly, so nothing has to be re-exposed for it.
        private class HitboxOverlay : MonoBehaviour
        {
            private void Awake() => useGUILayout = false; // no GUILayout here
            private void OnGUI() => DrawHitboxes();
        }

        private static Texture2D _hbTex;

        private static void DrawRect(float x, float yBottom, float w, float h, Color c)
        {
            // cache coords are bottom-left origin; GUI space is top-left
            GUI.color = c;
            GUI.DrawTexture(new Rect(x, Screen.height - yBottom - h, w, h), _hbTex);
        }

        private static void DrawBoxOutline(float cx, float cy, float half, Color c)
        {
            float t = 2f; // line thickness
            DrawRect(cx - half, cy + half - t, half * 2f, t, c); // top
            DrawRect(cx - half, cy - half, half * 2f, t, c);     // bottom
            DrawRect(cx - half, cy - half, t, half * 2f, c);     // left
            DrawRect(cx + half - t, cy - half, t, half * 2f, c); // right
        }

        // Arbitrary-angle 2px line between two bottom-left-origin screen points.
        private static void DrawLine(Vector2 a, Vector2 b, Color c)
        {
            var ga = new Vector2(a.x, Screen.height - a.y);
            var gb = new Vector2(b.x, Screen.height - b.y);
            Vector2 d = gb - ga;
            float len = d.magnitude;
            if (len < 0.5f) return;
            Matrix4x4 m = GUI.matrix;
            GUIUtility.RotateAroundPivot(Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg, ga);
            GUI.color = c;
            GUI.DrawTexture(new Rect(ga.x, ga.y - 1f, len, 2f), _hbTex);
            GUI.matrix = m;
        }

        private static void DrawCircle(float cx, float cy, float r, Color c)
        {
            const int Seg = 16; // enough to read as a circle at pet sizes
            var prev = new Vector2(cx + r, cy);
            for (int s = 1; s <= Seg; s++)
            {
                float a = s * (Mathf.PI * 2f / Seg);
                var pt = new Vector2(cx + Mathf.Cos(a) * r, cy + Mathf.Sin(a) * r);
                DrawLine(prev, pt, c);
                prev = pt;
            }
        }

        private static void DrawHitboxes()
        {
            if (Event.current.type != EventType.Repaint) return;

            if (_hbTex == null)
            {
                _hbTex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                _hbTex.SetPixel(0, 0, Color.white);
                _hbTex.Apply();
            }

            Color prev = GUI.color;

            // tiles: the rendered shape itself (mesh hull, rotation and all)
            // plus the walkable top envelope the landing check uses
            var tileLine = new Color(0.2f, 1f, 0.3f, 0.9f);
            var tileEdge = new Color(0.2f, 1f, 0.3f, 0.45f);
            for (int i = 0; i < _floorCount; i++)
            {
                int m = FHn[i];
                if (m > 0)
                {
                    int b = i * MaxHullPts;
                    for (int e = 0; e < m; e++)
                    {
                        int e1 = e + 1 == m ? 0 : e + 1;
                        DrawLine(new Vector2(FHx[b + e], FHy[b + e]),
                            new Vector2(FHx[b + e1], FHy[b + e1]), tileEdge);
                    }
                    // top envelope: sample across the hull's x-span
                    int steps = Mathf.Clamp((int)(FHalf[i] / 10f), 2, 24);
                    float x0 = Fx[i] + FCxOff[i] - FHalf[i] + 0.5f;
                    float dx = (FHalf[i] * 2f - 1f) / steps;
                    Vector2 prevPt = default; bool has = false;
                    for (int s = 0; s <= steps; s++)
                    {
                        float sx = x0 + dx * s;
                        float sy = HullTopAt(i, sx);
                        if (float.IsNegativeInfinity(sy)) { has = false; continue; }
                        var pt = new Vector2(sx, sy + 1f);
                        if (has) DrawLine(prevPt, pt, tileLine);
                        prevPt = pt; has = true;
                    }
                }
                else
                {
                    DrawRect(Fx[i] + FCxOff[i] - FHalf[i], Fy[i] + FTopOff[i] - 1.5f,
                        FHalf[i] * 2f, 3f, tileLine);
                }
            }

            // decorations: bright strip on the walkable top edge + center tick
            for (int i = 0; i < _decoCount; i++)
            {
                DrawRect(Dx[i] - DHalf[i], DTop[i] - 1.5f, DHalf[i] * 2f, 3f,
                    new Color(0.2f, 0.8f, 1f, 0.9f));
                DrawRect(Dx[i] - 1.5f, DTop[i] - 8f, 3f, 8f,
                    new Color(0.2f, 0.8f, 1f, 0.6f));
            }

            // planets: box outline around the ball circle + crosshair
            for (int i = 0; i < _planetCount; i++)
            {
                var c = new Color(1f, 0.3f, 0.9f, 0.8f);
                DrawBoxOutline(Plx[i], Ply[i], PlR[i], c);
                DrawRect(Plx[i] - 5f, Ply[i] - 1f, 10f, 2f, c);
                DrawRect(Plx[i] - 1f, Ply[i] - 5f, 2f, 10f, c);
            }

            // pets: the square the mouse grabs by (dimmed while the pet can't
            // be picked up) and the circle other pets bump into, coloured by
            // how this pet takes part in the collision pass
            var mgr = Instance;
            int petCount = mgr == null ? 0 : mgr._liveCount;
            if (mgr != null)
            {
                float grabHalf = PetActor.GrabHalf;
                float radius = PetActor.SharedRadius;
                for (int i = 0; i < mgr._liveCount; i++)
                {
                    var pet = mgr._live[i];
                    if (pet == null) continue;
                    Vector2 p = pet.Pos;
                    DrawBoxOutline(p.x, p.y, grabHalf,
                        new Color(1f, 0.9f, 0.2f, pet.Grabbable ? 0.8f : 0.25f));

                    PetActor.CollideKind kind = pet.Collidability();
                    // a pet whose group toggle is off collides with nobody
                    // right now, so it reads as a pass-through here too
                    if (kind != PetActor.CollideKind.None && !PetCollides(pet))
                        kind = PetActor.CollideKind.None;
                    Color c =
                        kind == PetActor.CollideKind.Movable ? new Color(1f, 0.5f, 0.1f, 0.85f) :
                        kind == PetActor.CollideKind.Static ? new Color(1f, 0.2f, 0.2f, 0.85f) :
                        new Color(0.6f, 0.6f, 0.6f, 0.35f); // passes through
                    DrawCircle(p.x, p.y, radius, c);
                }
            }

            GUI.color = new Color(1f, 1f, 1f, 0.9f);
            GUI.Label(new Rect(8f, 8f, 460f, 22f),
                $"Adomeji surfaces — tiles: {_floorCount}  decos: {_decoCount}  " +
                $"planets: {_planetCount}  pets: {petCount}");

            GUI.color = prev;
        }

        // ---------------- unified surface cache ----------------
        // Everything a pet can land on or leap to: tiles, planets, any visible
        // world sprite (decorations included), any UI element. Candidates are
        // rescanned every 0.25s; only candidates get projected each frame.

        private void UpdateSurfaceCache(float dt)
        {
            // last frame's screen positions feed the landing sweeps' surface-
            // motion term (see FDx/FDy); stash them before the rebuild
            _floorPrevPos.Clear();
            for (int i = 0; i < _floorCount; i++)
                if (!ReferenceEquals(Ft[i], null)) _floorPrevPos[Ft[i]] = new Vector2(Fx[i], Fy[i]);
            _decoPrevTop.Clear();
            for (int i = 0; i < _decoCount; i++)
                if (!ReferenceEquals(Dt[i], null)) _decoPrevTop[Dt[i]] = DTop[i];

            NoFloorActive = NoWallActive = NoCeilingActive = false;
            _floorCount = 0;
            _planetCount = 0;
            _decoCount = 0;
            _planetMoverCount = 0;
            _floorSlots.Clear();
            _scrW = _videoCapture ? _videoSize.x : Screen.width;
            _scrH = _videoCapture ? _videoSize.y : Screen.height;

            _cam = null;
            var cam = GameCamera();
            if (!_videoCapture) ApplyCanvasMode(cam);
            if (cam == null) return;
            _cam = cam;
            SetupProjection(cam);

            _floorScanTimer -= dt;
            if (_floorScanTimer <= 0f)
            {
                _floorScanTimer = 0.25f;
                RescanCandidates(cam);
                _screenTileFx = cam.GetComponent<ScreenTile>();
            }

            // pets drawn inside the camera's render get tiled by the ScreenTile
            // shader like everything else, so the manual remap must stay off —
            // it only compensates when pets render on top, after the filters
            bool stfx = !_canvasOnCamera && _screenTileFx != null && _screenTileFx.enabled;
            _dispTileX = stfx ? Mathf.Max(0.05f, _screenTileFx.tileX) : 1f;
            _dispTileY = stfx ? Mathf.Max(0.05f, _screenTileFx.tileY) : 1f;
            _displayTiled = _dispTileX <= 0.9999f || _dispTileX >= 1.0001f
                         || _dispTileY <= 0.9999f || _dispTileY >= 1.0001f;

            // orthographic camera: pixels-per-world-unit is uniform, probe once
            Vector3 camPos = cam.transform.position;
            Vector3 a = Project(camPos);
            Vector3 b = Project(camPos + cam.transform.right);
            _floorHalfWidthPx = Mathf.Max(6f, Vector2.Distance(a, b) * 0.5f);
            float ppu = _floorHalfWidthPx * 2f;

            float w = _scrW, h = _scrH;

            // zoom factor, resolution-normalized: ~100 px/unit at 1080p reads
            // as "normal"; zoomed-out cameras shrink the pets to match
            float dispAvg = (DisplayScaleX + DisplayScaleY) * 0.5f;
            _zoomFactor = Mathf.Clamp(ppu * dispAvg * (1080f / Mathf.Max(1f, h)) / 100f, 0.3f, 1.5f);

            int n = 0;
            for (int i = 0; i < _floorCands.Count && n < Fx.Length; i++)
            {
                var c = _floorCands[i];
                var f = c.Floor;
                // mid-animation ghosts aren't ground; decoration tiles answer
                // to the decoration's opacity, not the path tile fade
                var fdeco = c.Deco;
                if (f == null) continue;
                var t = c.T;
                if (fdeco == null ? !FloorVisible(f, c.Go, t)
                                  : !(DecoOpaque(fdeco) && c.Go.activeInHierarchy
                                      && Mathf.Abs(t.lossyScale.x) > 0.05f)) continue;
                Vector3 sp = Project(t.position);
                if (sp.z < 0f || sp.x < -40f || sp.x > w + 40f || sp.y < -40f || sp.y > h + 40f) continue;
                float ax = sp.x, ay = sp.y; // display-cell anchor for this tile
                sp.x = DisplayMapX(sp.x, ax); sp.y = DisplayMapY(sp.y, ay);
                Fx[n] = sp.x; Fy[n] = sp.y; Ft[n] = t; Ff[n] = f;
                Fidx[n] = c.PathIndex;
                _floorSlots[Fidx[n]] = n;

                // screen delta since last frame. Teleports (camera snaps, beat
                // swaps) aren't motion — don't sweep landings across them.
                FDx[n] = 0f; FDy[n] = 0f;
                if (_floorPrevPos.TryGetValue(t, out Vector2 fpp))
                {
                    float fdx = sp.x - fpp.x, fdy = sp.y - fpp.y;
                    float tele = _floorHalfWidthPx * 20f;
                    if (fdx * fdx + fdy * fdy < tele * tele) { FDx[n] = fdx; FDy[n] = fdy; }
                }

                // measured tile footprint: the tile AS RENDERED — the mesh
                // hull through the full transform, so baked-in rotation shows
                // up exactly as drawn. Its projection feeds the top-envelope
                // surface math.
                var rend = c.Rend;
                var hull = c.Hull;
                bool hullOk = false;
                if (rend != null && hull != null)
                {
                    // The hot loop of the whole mod: every tile on screen times
                    // up to 32 outline points, every frame. One combined
                    // local->clip matrix per tile, and the point math is
                    // written out inline — hull points are flat (local z = 0),
                    // so that whole column of the matrix drops away, and a
                    // Matrix4x4.MultiplyPoint call per point is a managed call
                    // Mono will not inline. Depth is judged once, on the tile's
                    // anchor above: an outline corner cannot straddle the
                    // camera plane on its own under an orthographic camera.
                    Matrix4x4 m = _vpM * c.RendT.localToWorldMatrix;
                    hullOk = true;
                    float minX = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                    int b0 = n * MaxHullPts;
                    for (int k = 0; k < hull.Length; k++)
                    {
                        float hx = hull[k].x, hy = hull[k].y;
                        float cw = m.m30 * hx + m.m31 * hy + m.m33;
                        if (cw > -1e-6f && cw < 1e-6f) { hullOk = false; break; }
                        float inv = 1f / cw;
                        float px = _vpX
                            + ((m.m00 * hx + m.m01 * hy + m.m03) * inv * 0.5f + 0.5f) * _vpW;
                        float py = _vpY
                            + ((m.m10 * hx + m.m11 * hy + m.m13) * inv * 0.5f + 0.5f) * _vpH;
                        if (_displayTiled)
                        {
                            px = DisplayMapX(px, ax); py = DisplayMapY(py, ay);
                        }
                        FHx[b0 + k] = px; FHy[b0 + k] = py;
                        if (px < minX) minX = px;
                        if (px > maxX) maxX = px;
                        if (py > maxY) maxY = py;
                    }
                    if (hullOk)
                    {
                        FHn[n] = hull.Length;
                        FHalf[n] = Mathf.Max(6f, (maxX - minX) * 0.5f);
                        FCxOff[n] = (minX + maxX) * 0.5f - sp.x;
                        FTopOff[n] = Mathf.Max(0f, maxY - sp.y);
                    }
                }
                if (!hullOk)
                {
                    FHn[n] = 0;
                    if (rend != null)
                    {
                        var bnd = rend.bounds;
                        Vector3 tp = Project(
                            new Vector3(bnd.center.x, bnd.max.y, bnd.center.z));
                        tp.x = DisplayMapX(tp.x, ax); tp.y = DisplayMapY(tp.y, ay);
                        FHalf[n] = Mathf.Max(6f, bnd.extents.x * ppu * 0.8f * DisplayScaleX);
                        FTopOff[n] = Mathf.Max(0f, tp.y - sp.y);
                        FCxOff[n] = tp.x - sp.x;
                    }
                    else
                    {
                        FHalf[n] = Mathf.Max(6f, _floorHalfWidthPx * (TileHalfLenUnits * 2f) * DisplayScaleX);
                        FTopOff[n] = _floorHalfWidthPx * (TileHalfThickUnits * 2f) * DisplayScaleY;
                        FCxOff[n] = 0f;
                    }
                }
                n++;
            }
            _floorCount = n;

            n = 0;
            for (int i = 0; i < _planetBalls.Count && n < Plx.Length; i++)
            {
                var t = _planetBalls[i];
                if (t == null) continue;
                // planet object decorations can fade via events mid-scan-window
                if (_planetBallDecos[i] != null && !DecoOpaque(_planetBallDecos[i])) continue;
                Vector3 sp = Project(t.position); // the actual ball
                if (sp.z < 0f || sp.x < -40f || sp.x > w + 40f || sp.y < -40f || sp.y > h + 40f) continue;
                sp.x = DisplayMapX(sp.x, sp.x); sp.y = DisplayMapY(sp.y, sp.y);
                Plx[n] = sp.x; Ply[n] = sp.y; Plt[n] = t;
                PlRootT[n] = _planetRoots[i];
                // standard planet sprite is ~1 world unit across, times its
                // scale; trimmed a little so pets sit on the ball rather than
                // on its outer glow
                PlR[n] = Mathf.Max(_floorHalfWidthPx * 0.6f,
                    0.5f * Mathf.Abs(t.lossyScale.x) * ppu) * dispAvg * PlanetRadiusKeep;
                // screen velocity + swept segment vs last frame, matched by transform
                PlVx[n] = 0f; PlVy[n] = 0f;
                PlSegX[n] = sp.x; PlSegY[n] = sp.y;
                if (dt > 0f)
                {
                    for (int j = 0; j < _planetPrevCount; j++)
                    {
                        if (PlPrevT[j] != t) continue;
                        float segX = sp.x - PlPrevX[j], segY = sp.y - PlPrevY[j];
                        // beat swaps / camera snaps teleport things; a teleport
                        // is not a swing, so don't sweep or fling across it
                        float teleport = _floorHalfWidthPx * 20f;
                        if (segX * segX + segY * segY < teleport * teleport)
                        {
                            PlVx[n] = segX / dt;
                            PlVy[n] = segY / dt;
                            PlSegX[n] = PlPrevX[j];
                            PlSegY[n] = PlPrevY[j];
                        }
                        break;
                    }
                }
                n++;
            }
            _planetCount = n;
            float planetMinSpeed = _floorHalfWidthPx * 3f; // ~1.5 world units/s
            for (int i = 0; i < n; i++)
            {
                PlPrevX[i] = Plx[i]; PlPrevY[i] = Ply[i]; PlPrevT[i] = Plt[i];
                if (PlVx[i] * PlVx[i] + PlVy[i] * PlVy[i] >= planetMinSpeed * planetMinSpeed)
                    PlMover[_planetMoverCount++] = i;
            }
            _planetPrevCount = n;

            n = 0;
            for (int i = 0; i < _decoCandidates.Count && n < MaxDecos; i++)
            {
                if (!DecoOpaque(_decoCandidateDecos[i])) continue;
                if (!CombinedBounds(_decoCandidates[i], out Bounds bnd)) continue;
                Vector3 csp = Project(bnd.center);
                if (csp.z < 0f) continue;
                float halfPx = bnd.extents.x * ppu * DisplayScaleX;
                // specks aren't platforms; screen-filling backdrops aren't either
                if (halfPx < 8f || halfPx > w * 1.2f) continue;
                Vector3 tsp = Project(
                    new Vector3(bnd.center.x, bnd.max.y, bnd.center.z));
                if (csp.x < -40f || csp.x > w + 40f || tsp.y < -40f || tsp.y > h + 40f) continue;
                float dax = csp.x, day = csp.y;
                csp.x = DisplayMapX(csp.x, dax); csp.y = DisplayMapY(csp.y, day);
                tsp.x = DisplayMapX(tsp.x, dax); tsp.y = DisplayMapY(tsp.y, day);
                Dx[n] = csp.x; DTop[n] = tsp.y; DHalf[n] = halfPx;
                Dt[n] = _decoCandidateDecos[i].transform;
                // top-edge delta since last frame, teleport-guarded like FDy
                DDy[n] = 0f;
                if (_decoPrevTop.TryGetValue(Dt[n], out float dpt))
                {
                    float ddy = tsp.y - dpt;
                    float tele = _floorHalfWidthPx * 20f;
                    if (ddy * ddy < tele * tele) DDy[n] = ddy;
                }
                n++;
            }
            _decoCount = n;

            NoFloorActive = RuntimeContext.Settings.NoFloor
                && (_floorCount > 0 || _planetCount > 0 || _decoCount > 0);
            NoWallActive = RuntimeContext.Settings.NoWall;
            NoCeilingActive = RuntimeContext.Settings.NoCeiling;
        }

        // FindObjectsByType walks the whole scene and allocates; the set of
        // PlanetarySystems changes about never mid-scene, so hold the result
        // and refresh at a slow cadence (destroyed entries turn into Unity
        // nulls, which the consumers already skip).
        private PlanetarySystem[] _systemsCache;
        private float _systemsScanTimer;

        // pathIndex < 0 marks a decoration tile: no path neighbors to stroll to
        private void AddFloorCandidate(scrFloor f, int pathIndex, scrDecoration deco)
        {
            if (f == null || _floorCands.Count >= Fx.Length) return;
            if (!_floorMemo.TryGetValue(f, out var c))
            {
                var fr = FloorRendererOf(f);
                c = new FloorCand
                {
                    Floor = f,
                    T = f.transform,
                    Go = f.gameObject,
                    Rend = fr,
                    RendT = fr != null ? fr.transform : null,
                    Mesh = fr != null ? fr.GetComponent<FloorMesh>() : null,
                };
                _floorMemo[f] = c;
            }
            c.PathIndex = pathIndex;
            c.Deco = deco;
            RefreshHull(c);
            _floorCands.Add(c);
        }

        // Rebuild a tile's outline only when its shape actually changed.
        // FloorMesh rewrites cacheKey whenever the slab's angles, size or track
        // style change, so comparing that one field skips the style-key string
        // and the hull lookup for every tile on every rescan.
        // ponytail: keyed on the tile's own cacheKey only. If HullOf had to
        // settle for it because the default-style polygon wasn't in
        // FloorMesh.cache yet, a later arrival won't trigger a rebuild (it
        // used to, on the next rescan) — the outline is then the styled slab
        // rather than the default one, a small thickness difference. Key on
        // the default-style key too if that ever shows.
        private const string StaticShapeKey = "static"; // sprite / raw-mesh tiles

        private static void RefreshHull(FloorCand c)
        {
            string key = c.Mesh != null ? c.Mesh.cacheKey : StaticShapeKey;
            if (c.Hull != null && key == c.HullKey) return;
            c.HullKey = key;
            c.Hull = HullOf(c.Rend, c.Floor);
        }

        // Where the last scan found tiles, and how many scans until the next
        // exhaustive sweep. listFloors runs to tens of thousands of entries in
        // long charts and the old scan walked ALL of them from index 0 four
        // times a second — a hitch that grew the further into the level the
        // player got. The camera only ever sees a contiguous-ish run of the
        // path, so the scan now starts where the last one found tiles and
        // walks outward until it has gone ScanMissBudget tiles past the last
        // hit in each direction.
        private int _scanHint;
        private int _fullScanCountdown;
        private const int ScanMissBudget = 600;  // tiles past the last hit before a side gives up
        private const int FullScanEvery = 4;     // rescans between exhaustive sweeps (~1s)

        // ponytail: a windowed scan can miss a tile that renders inside the
        // camera rect while sitting far away in the path (Move Track dragging
        // one lone tile across the screen). The periodic full sweep picks it
        // up within a second, and an empty window forces one immediately.
        private void ScanFloors(Camera cam, List<scrFloor> floors)
        {
            int count = floors.Count;
            if (count == 0) return;
            Vector3 c = cam.transform.position;
            float halfH = cam.orthographicSize + 2f;
            float halfW = cam.orthographicSize * cam.aspect + 2f;

            bool full = --_fullScanCountdown <= 0;
            if (full) _fullScanCountdown = FullScanEvery;

            int start = _scanHint < 0 ? 0 : _scanHint >= count ? count - 1 : _scanHint;
            if (!ScanFloorsFrom(floors, start, full, c, halfW, halfH) && !full)
            {
                // window came up empty (camera cut, level restart, stale hint):
                // sweep exhaustively right now rather than leaving the pets
                // with nothing to stand on until the next scheduled sweep
                _fullScanCountdown = FullScanEvery;
                ScanFloorsFrom(floors, start, true, c, halfW, halfH);
            }
        }

        // Walks outward from `start`, one step forward and one step back per
        // round, so a level dense enough to fill the candidate cap spends it
        // evenly on both sides of the camera instead of only ahead. `full`
        // drops the miss budget, making it an exhaustive sweep. Returns
        // whether anything was found, and leaves _scanHint on the middle of
        // the run it found.
        private bool ScanFloorsFrom(List<scrFloor> floors, int start, bool full,
            Vector3 c, float halfW, float halfH)
        {
            int count = floors.Count;
            int cap = Fx.Length;
            int fi = start, bi = start - 1;
            int fMiss = 0, bMiss = 0;
            bool fOpen = true, bOpen = bi >= 0;
            int lo = start, hi = start;
            bool any = false;

            while ((fOpen || bOpen) && _floorCands.Count < cap)
            {
                if (fOpen)
                {
                    if (fi >= count) fOpen = false;
                    else
                    {
                        if (TryAddFloorAt(floors, fi, c, halfW, halfH))
                        {
                            fMiss = 0; hi = fi; any = true;
                        }
                        else if (!full && ++fMiss > ScanMissBudget) fOpen = false;
                        fi++;
                    }
                }
                if (bOpen && _floorCands.Count < cap)
                {
                    if (bi < 0) bOpen = false;
                    else
                    {
                        if (TryAddFloorAt(floors, bi, c, halfW, halfH))
                        {
                            bMiss = 0; lo = bi; any = true;
                        }
                        else if (!full && ++bMiss > ScanMissBudget) bOpen = false;
                        bi--;
                    }
                }
            }

            if (any) _scanHint = (lo + hi) / 2;
            return any;
        }

        private bool TryAddFloorAt(List<scrFloor> floors, int i, Vector3 c,
            float halfW, float halfH)
        {
            var f = floors[i];
            if (f == null) return false;
            Vector3 p = f.transform.position;
            if (p.x < c.x - halfW || p.x > c.x + halfW) return false;
            if (p.y < c.y - halfH || p.y > c.y + halfH) return false;
            AddFloorCandidate(f, i, null);
            return true;
        }

        private void RescanCandidates(Camera cam)
        {
            _floorCands.Clear();
            var lm = scrLevelMaker.instance;
            // new level (or an implausible pile-up): the memo's tiles are gone
            bool levelChanged = !ReferenceEquals(lm, _lastLevelMaker);
            if (levelChanged || _floorMemo.Count > 1024 || _decoMemo.Count > 1024)
            {
                _lastLevelMaker = lm;
                _floorMemo.Clear();
                _decoMemo.Clear();
                _scanHint = 0;
            }
            if (lm != null && lm.listFloors != null) ScanFloors(cam, lm.listFloors);

            _planetBalls.Clear();
            _planetRoots.Clear();
            _planetBallDecos.Clear();

            // The scene always holds 8 scrPlanet objects: red, blue, and six
            // spares that PlanetarySystem.Init() instantiates and immediately
            // hides (scrPlanet.Destroy only turns the sprite off — the object
            // stays active and findable). Riding those means clinging to an
            // invisible ball, so take the in-play list when we can get it and
            // fall back to a visibility check on a raw scan.
            // FindObjectsByType walks every object in the scene — in a level
            // that is one crawl over tens of thousands of GameObjects, and on
            // a 2s timer it was a periodic hitch of its own. The set only
            // changes when the level does, so rescan on a level change, on a
            // dead entry, or on a long safety-net timer.
            bool refresh = _systemsCache == null || levelChanged
                || Time.unscaledTime >= _systemsScanTimer || AnySystemDead();
            if (refresh)
            {
                _systemsScanTimer = Time.unscaledTime + 30f;
                _systemsCache = FindObjectsByType<PlanetarySystem>(FindObjectsSortMode.None);
            }
            var systems = _systemsCache;
            for (int s = 0; s < systems.Length; s++)
            {
                var list = systems[s] != null ? systems[s].planetList : null;
                if (list == null) continue;
                for (int i = 0; i < list.Count; i++) AddPlanet(list[i]);
            }

            if (_planetBalls.Count == 0)
            {
                if (refresh || _planetFallbackCache == null)
                    _planetFallbackCache = FindObjectsByType<scrPlanet>(FindObjectsSortMode.None);
                var planets = _planetFallbackCache;
                for (int i = 0; i < planets.Length; i++) AddPlanet(planets[i]);
            }

            // object decorations near the camera (the built-in props placed
            // with AddObject). Same two-tier deal as tiles: cheap world-rect
            // filter here, projection per frame. Floor objects become flat
            // walkable surfaces; planet objects join the planet cache and get
            // orbited like the real thing. Image/text/prefab/particle
            // decorations are art direction, not architecture — skipped.
            _decoCandidates.Clear();
            _decoCandidateDecos.Clear();
            var dm = scrDecorationManager.instance;
            if (dm != null && dm.allDecorations != null)
            {
                Vector3 c = cam.transform.position;
                float halfH = cam.orthographicSize + 2f;
                float halfW = cam.orthographicSize * cam.aspect + 2f;
                var decos = dm.allDecorations;
                for (int i = 0; i < decos.Count && _decoCandidates.Count < MaxDecos; i++)
                {
                    var od = decos[i] as scrObjectDecoration;
                    if (od == null) continue;
                    // Transform / GameObject / renderer-set lookups are native
                    // calls (GetComponentsInChildren allocates an array on top),
                    // and none of them change while a prop sits there — resolve
                    // once per decoration and hold it across rescans.
                    if (!_decoMemo.TryGetValue(od, out var dc))
                    {
                        dc = new DecoCand { T = od.transform, Go = od.gameObject };
                        _decoMemo[od] = dc;
                    }
                    if (!dc.Go.activeInHierarchy) continue;
                    Vector3 p = dc.T.position;
                    if (p.x < c.x - halfW || p.x > c.x + halfW) continue;
                    if (p.y < c.y - halfH || p.y > c.y + halfH) continue;

                    if (od.objectType == ObjectDecorationType.Planet)
                    {
                        // round prop: rides like a planet, not a platform.
                        // Same hidden-ball checks as AddPlanet.
                        var opr = od.planetRenderer;
                        if (opr == null || opr.onlyRing) continue;
                        if (opr.sprite != null && !opr.sprite.visible) continue;
                        if (_planetBalls.Count >= Plx.Length) continue;
                        bool dup = false;
                        for (int j = 0; j < _planetRoots.Count; j++)
                            if (_planetRoots[j] == dc.T) { dup = true; break; }
                        if (dup) continue;
                        _planetBalls.Add(opr.transform);
                        _planetRoots.Add(dc.T);
                        _planetBallDecos.Add(od);
                        continue;
                    }

                    if (od.objectType != ObjectDecorationType.Floor) continue; // bubbles: no

                    // a floor object decoration IS a tile — same mesh, same
                    // outline math. Give it a synthetic (negative) path index
                    // so it never collides with a real listFloors entry, and
                    // never tries to walk onto "neighbors" it doesn't have.
                    if (od.floor != null && FloorRendererOf(od.floor) != null)
                    {
                        AddFloorCandidate(od.floor, -1 - _floorCands.Count, od);
                        continue;
                    }

                    var rends = dc.Rends ?? (dc.Rends = od.GetComponentsInChildren<Renderer>());
                    if (rends.Length == 0) continue;
                    _decoCandidates.Add(rends);
                    _decoCandidateDecos.Add(od);
                }
            }
        }

        private scrPlanet[] _planetFallbackCache;

        // A destroyed PlanetarySystem turns into a Unity null: the cache is
        // stale and the scene walk has to run again.
        private bool AnySystemDead()
        {
            var s = _systemsCache;
            for (int i = 0; i < s.Length; i++)
                if (s[i] == null) return true;
            return false;
        }

        // A planet counts only while it's actually on screen as a ball.
        private void AddPlanet(scrPlanet p)
        {
            if (p == null || !p.gameObject.activeInHierarchy) return;
            if (_planetBalls.Count >= Plx.Length - 1) return;

            var pr = p.planetRenderer;
            if (pr == null) return;
            // hidden (spare / exploded) planets: sprite invisible, ring only
            if (pr.sprite != null && !pr.sprite.visible) return;
            if (pr.onlyRing) return;

            var root = p.transform;
            for (int i = 0; i < _planetRoots.Count; i++)
                if (_planetRoots[i] == root) return; // already listed

            _planetBalls.Add(root);
            _planetRoots.Add(root);
            _planetBallDecos.Add(null);
            if (pr.transform != root && _planetBalls.Count < Plx.Length)
            {
                _planetBalls.Add(pr.transform);
                _planetRoots.Add(root);
                _planetBallDecos.Add(null);
            }
        }

        // px per world unit, from this frame's camera probe
        public static float PixelsPerUnit => _floorHalfWidthPx * 2f;

        // The gameplay camera. Camera.main is unreliable here: charts with
        // screen-filter effects flip ADOFAI into RenderTexture mode, which
        // enables extra cameras (overlay quad, pause planets) that can win
        // the Camera.main lookup — then every projection lands off screen
        // and the whole surface cache silently empties (pets stop landing
        // on planets and planets stop hitting pets). Ask scrCamera for the
        // real one; fall back to Camera.main in scenes that don't have it.
        public static Camera GameCamera()
        {
            if (_cam != null) return _cam; // resolved once per frame by the cache
            var sc = scrCamera.instance;
            if (sc != null && sc.camobj != null && sc.camobj.isActiveAndEnabled)
                return sc.camobj;
            return Camera.main;
        }

        // The renderer that actually draws a tile. Newer builds route it
        // through FloorRenderer (mesh or sprite flavour); older ones keep a
        // plain SpriteRenderer on the tile itself. Either way this is the
        // object whose bounds describe the walkable slab.
        private static Renderer FloorRendererOf(scrFloor f)
        {
            if (f == null) return null;
            var fr = f.floorRenderer;
            if (fr != null && fr.renderer != null) return fr.renderer;
            if (f.legacyFloorSpriteRenderer != null) return f.legacyFloorSpriteRenderer;
            return f.GetComponent<Renderer>();
        }

        // The local-space silhouette of what a tile renderer draws. FloorMesh
        // tiles: convex hull of the mesh vertices (the slab's angle is baked
        // into them — this is the ONLY place the rendered shape lives).
        // Sprite tiles: the sprite's corner quad (their rotation lives on the
        // transform, which the projection applies).
        // simplified outlines, keyed by the FloorMesh cache key they came from
        // (one entry per distinct tile SHAPE, not per tile)
        private static readonly Dictionary<string, Vector2[]> _hullCache =
            new Dictionary<string, Vector2[]>();

        // FloorMesh's own cache key for this tile shape as the DEFAULT track
        // style would build it. SetTrackStyle pads the slab per style
        // (Basic/Neon/... add 0.0375 to the width, Minimal trims the length),
        // so the cached outline differs per skin; the default branch uses the
        // bare base dimensions. Same key format as FloorMesh.UpdateMesh.
        // Null when the tile is already default-styled or the data is missing.
        private static string DefaultStyleKey(FloorMesh fm, scrFloor f)
        {
            var c = scrController.instance;
            if (c == null || f == null) return null;
            Vector2 baseDim = c.baseFloorDimensions;
            float defLen = baseDim.x * f.lengthMult;
            float defWid = baseDim.y * f.widthMult;
            if (Mathf.Approximately(defWid, fm._width)
                && Mathf.Approximately(defLen, fm._length)) return null;
            return $"{fm._angle0},{fm._angle1},{defWid},{defLen},{fm._curvaturePoints}";
        }

        private static Vector2[] HullOf(Renderer rend, scrFloor f)
        {
            if (rend == null) return null;
            // FloorMesh tiles: the game caches the EXACT tile outline polygon
            // (the same one it assigns to the editor's PolygonCollider2D) in
            // FloorMesh.cache, keyed by the tile's public cacheKey. That's the
            // authoritative shape — angles, corners, U-turns, no shadow or
            // glow padding. Styles pad/trim the slab width (SetTrackStyle sets
            // a per-style _width), so the polygon is scaled along its
            // thickness back to the DEFAULT style's width — same hitbox no
            // matter the skin.
            var fm = rend.GetComponent<FloorMesh>();
            if (fm != null && !string.IsNullOrEmpty(fm.cacheKey))
            {
                // prefer the polygon the DEFAULT style would have drawn for
                // this same tile shape; fall back to the tile's own
                string key = DefaultStyleKey(fm, f);
                if (key == null || !FloorMesh.cache.ContainsKey(key)) key = fm.cacheKey;
                if (_hullCache.TryGetValue(key, out var cached)) return cached;
                if (FloorMesh.cache.TryGetValue(key, out var mc)
                    && mc != null && mc.polygon != null && mc.polygon.Length >= 3)
                {
                    var poly = SimplifyPolygon(new List<Vector2>(mc.polygon), MaxHullPts);
                    _hullCache[key] = poly;
                    return poly;
                }
            }
            var mf = rend.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null && mf.sharedMesh.vertexCount >= 3)
                return TopProfileOf(mf.sharedMesh);
            var sr = rend as SpriteRenderer;
            if (sr != null && sr.sprite != null)
            {
                // sprite track styles vary wildly in how much halo/padding the
                // texture carries — ignore all of it and use the standard tile
                // cell: 1 world unit long, nominal slab thickness, centered on
                // the node. Same hitbox as every other tile. The transform
                // still applies the tile's rotation.
                return ShrinkThickness(new[]
                {
                    new Vector2(-0.5f, -TileHalfThickUnits),
                    new Vector2(0.5f, -TileHalfThickUnits),
                    new Vector2(0.5f, TileHalfThickUnits),
                    new Vector2(-0.5f, TileHalfThickUnits),
                }, TileThickKeep);
            }
            return null;
        }

        // Tile meshes/sprites carry a small border margin beyond the visible
        // slab face — keep this fraction of the hull's thickness (measured
        // perpendicular to its longest edge, so rotated slabs trim correctly;
        // length stays untouched).
        private const float TileThickKeep = 0.85f;

        private static Vector2[] ShrinkThickness(Vector2[] pts, float keep)
        {
            if (pts == null || pts.Length < 3) return pts;
            float bestLen = -1f;
            Vector2 dir = Vector2.right;
            for (int i = 0; i < pts.Length; i++)
            {
                Vector2 e = pts[(i + 1) % pts.Length] - pts[i];
                float l = e.sqrMagnitude;
                if (l > bestLen) { bestLen = l; dir = e; }
            }
            dir.Normalize();
            var nrm = new Vector2(-dir.y, dir.x);
            float lo = float.MaxValue, hi = float.MinValue;
            for (int i = 0; i < pts.Length; i++)
            {
                float p = Vector2.Dot(pts[i], nrm);
                if (p < lo) lo = p;
                if (p > hi) hi = p;
            }
            float mid = (lo + hi) * 0.5f;
            for (int i = 0; i < pts.Length; i++)
            {
                float p = Vector2.Dot(pts[i], nrm);
                pts[i] -= nrm * ((p - mid) * (1f - keep));
            }
            return pts;
        }

        // A tile mesh's silhouette as a closed polygon: top and bottom
        // envelopes sampled across its width. Unlike a convex hull, this keeps
        // the CONCAVE outline of corner / U-turn / midspin tiles — a convex
        // hull spans their notch with a giant diagonal and pets walk on air.
        // The border-margin trim is folded in per sample, as a fraction of the
        // local thickness at that x.
        private const int ProfileSamples = 24;

        private static Vector2[] TopProfileOf(Mesh mesh)
        {
            var verts = mesh.vertices;
            var tris = mesh.triangles;
            if (verts.Length < 3 || tris.Length < 3) return null;
            float minX = float.MaxValue, maxX = float.MinValue;
            for (int i = 0; i < verts.Length; i++)
            {
                if (verts[i].x < minX) minX = verts[i].x;
                if (verts[i].x > maxX) maxX = verts[i].x;
            }
            float span = maxX - minX;
            if (span < 1e-4f) return null;

            var top = new float[ProfileSamples + 1];
            var bot = new float[ProfileSamples + 1];
            for (int s = 0; s <= ProfileSamples; s++)
            {
                top[s] = float.NegativeInfinity;
                bot[s] = float.PositiveInfinity;
            }

            for (int t = 0; t < tris.Length; t += 3)
            {
                for (int e = 0; e < 3; e++)
                {
                    Vector3 a = verts[tris[t + e]];
                    Vector3 b = verts[tris[t + (e + 1) % 3]];
                    float lox = Mathf.Min(a.x, b.x), hix = Mathf.Max(a.x, b.x);
                    int s0 = Mathf.Max(0, Mathf.CeilToInt((lox - minX) / span * ProfileSamples));
                    int s1 = Mathf.Min(ProfileSamples,
                        Mathf.FloorToInt((hix - minX) / span * ProfileSamples));
                    float dx = b.x - a.x;
                    for (int s = s0; s <= s1; s++)
                    {
                        float x = minX + span * s / ProfileSamples;
                        if (Mathf.Abs(dx) < 1e-6f)
                        {
                            if (Mathf.Max(a.y, b.y) > top[s]) top[s] = Mathf.Max(a.y, b.y);
                            if (Mathf.Min(a.y, b.y) < bot[s]) bot[s] = Mathf.Min(a.y, b.y);
                        }
                        else
                        {
                            float y = a.y + (b.y - a.y) * ((x - a.x) / dx);
                            if (y > top[s]) top[s] = y;
                            if (y < bot[s]) bot[s] = y;
                        }
                    }
                }
            }

            // closed polygon: top profile left -> right, bottom right -> left,
            // each pulled inward by the margin trim
            var pts = new List<Vector2>((ProfileSamples + 1) * 2);
            for (int s = 0; s <= ProfileSamples; s++)
            {
                if (float.IsNegativeInfinity(top[s]) || float.IsPositiveInfinity(bot[s])) continue;
                float trim = Mathf.Min((top[s] - bot[s]) * (1f - TileThickKeep) * 0.5f, 0.05f);
                pts.Add(new Vector2(minX + span * s / ProfileSamples, top[s] - trim));
            }
            for (int s = ProfileSamples; s >= 0; s--)
            {
                if (float.IsNegativeInfinity(top[s]) || float.IsPositiveInfinity(bot[s])) continue;
                float trim = Mathf.Min((top[s] - bot[s]) * (1f - TileThickKeep) * 0.5f, 0.05f);
                pts.Add(new Vector2(minX + span * s / ProfileSamples, bot[s] + trim));
            }
            if (pts.Count < 3) return null;
            return SimplifyPolygon(pts, MaxHullPts);
        }

        // Drop near-collinear vertices (straight slabs collapse to ~4 points),
        // then uniform-downsample if still over budget.
        private static Vector2[] SimplifyPolygon(List<Vector2> pts, int maxPts)
        {
            int n = pts.Count;
            var keep = new List<Vector2>(n);
            for (int i = 0; i < n; i++)
            {
                Vector2 p = pts[(i - 1 + n) % n], c = pts[i], q = pts[(i + 1) % n];
                if (TriArea(p, c, q) > 1e-6f) keep.Add(c);
            }
            if (keep.Count < 3) keep = pts;

            // Over budget: drop the LEAST significant vertex (smallest triangle
            // with its neighbors) until it fits. Sampling by index instead
            // would throw away a slab's actual corners whenever a rounded
            // corner hogs most of the points — the outline then short-circuits
            // across the tile as one long diagonal.
            while (keep.Count > maxPts)
            {
                int worst = 0;
                float worstArea = float.MaxValue;
                int m = keep.Count;
                for (int i = 0; i < m; i++)
                {
                    float a = TriArea(keep[(i - 1 + m) % m], keep[i], keep[(i + 1) % m]);
                    if (a < worstArea) { worstArea = a; worst = i; }
                }
                keep.RemoveAt(worst);
            }
            return keep.ToArray();
        }

        private static float TriArea(Vector2 p, Vector2 c, Vector2 q) =>
            Mathf.Abs((c.x - p.x) * (q.y - p.y) - (c.y - p.y) * (q.x - p.x));

        // Top of the rendered shape at a given screen x: the upper envelope of
        // the projected hull — for every edge spanning x, take the highest
        // interpolated y. This is what makes rotated slabs walkable as drawn.
        // NegativeInfinity = x is outside the shape entirely.
        // Called several times per rider and twice per tile per falling pet, so
        // the span test is written out rather than routed through Mathf.
        private static float HullTopAt(int cacheIdx, float x)
            => SurfaceGeometry.TopAt(FHx, FHy, cacheIdx * MaxHullPts, FHn[cacheIdx], x);

        // ---- surface-cache slot lookups ----
        // A rider resolves its slot once per frame and then reads everything
        // (screen position, metrics, surface height, neighbors) by index.

        // Slot for the tile a pet is standing on. The index map is the fast
        // path; the transform is the authority, since decoration tiles get
        // synthetic indices that are only stable within one scan.
        public static int FloorSlot(int listIndex, Transform t)
        {
            if (_floorSlots.TryGetValue(listIndex, out int s)
                && s < _floorCount && ReferenceEquals(Ft[s], t)) return s;
            for (int i = 0; i < _floorCount; i++)
                if (ReferenceEquals(Ft[i], t)) return i;
            return -1;
        }

        public static int PlanetSlot(Transform t)
        {
            for (int i = 0; i < _planetCount; i++)
                if (ReferenceEquals(Plt[i], t)) return i;
            return -1;
        }

        public static int DecoSlot(Transform t)
        {
            for (int i = 0; i < _decoCount; i++)
                if (ReferenceEquals(Dt[i], t)) return i;
            return -1;
        }

        public static Vector2 FloorScreenPos(int slot) => new Vector2(Fx[slot], Fy[slot]);
        public static Vector2 PlanetScreenPos(int slot) => new Vector2(Plx[slot], Ply[slot]);
        public static float PlanetRadius(int slot) => PlR[slot];

        // decoration surface: bounds center x, bounds TOP y — what a pet stands on
        public static Vector2 DecoScreenPos(int slot) => new Vector2(Dx[slot], DTop[slot]);
        public static float DecoHalfWidth(int slot) => DHalf[slot];

        // The scrFloor of a cached tile, so re-anchoring mid-stroll doesn't
        // have to go back to listFloors and GetComponent.
        public static scrFloor FloorOf(int slot) => Ff[slot];
        public static Transform FloorTransform(int slot) => Ft[slot];
        public static int FloorIndexOf(int slot) => Fidx[slot];

        // Surface height under a pet standing on a cached tile.
        // False = no usable hull, or x is off the end of the slab.
        public static bool FloorSurfaceTopAt(int slot, float screenX, out float topScreenY)
        {
            topScreenY = 0f;
            if (slot < 0 || FHn[slot] == 0) return false;
            float y = HullTopAt(slot, screenX);
            if (float.IsNegativeInfinity(y)) return false;
            topScreenY = y;
            return true;
        }

        // Surface geometry of a cached tile: how far its top sits above the
        // node (px) and how far one can walk from the node before running out
        // of tile (px). slot < 0 falls back to the nominal tile size.
        public static void FloorMetrics(int slot, out float halfPx, out float topOff,
            out float centerOff)
        {
            if (slot >= 0)
            {
                halfPx = FHalf[slot];
                topOff = FTopOff[slot];
                centerOff = FCxOff[slot];
                return;
            }
            halfPx = Mathf.Max(6f, _floorHalfWidthPx * (TileHalfLenUnits * 2f));
            topOff = _floorHalfWidthPx * (TileHalfThickUnits * 2f);
            centerOff = 0f;
        }

        // The adjacent path tile on the given screen-x side (+1 right, -1
        // left), if it's cached and actually adjacent. Consecutive path tiles
        // sit ~1 world unit apart; a farther neighbor (path jump, camera cut)
        // is a real gap — walking toward it would mean strolling through open
        // air, so it doesn't count.
        public static bool NeighborSlot(int slot, int side, out Vector2 nb, out int nbSlot)
        {
            nb = default; nbSlot = -1;
            int idx = Fidx[slot];
            if (idx < 0) return false; // decoration tile: no path to stroll along
            float cx = Fx[slot], cy = Fy[slot];
            float ppu = _floorHalfWidthPx * 2f;
            float mx = ppu * 1.6f * DisplayScaleX, my = ppu * 1.6f * DisplayScaleY;

            for (int step = 1; step >= -1; step -= 2)
            {
                if (!_floorSlots.TryGetValue(idx + step, out int s) || s >= _floorCount) continue;
                float x = Fx[s], y = Fy[s];
                int sgn = x > cx ? 1 : x < cx ? -1 : 0;
                if (sgn != side) continue; // wrong side (or straight up)
                if (Mathf.Abs(x - cx) > mx || Mathf.Abs(y - cy) > my) continue;
                nb = new Vector2(x, y);
                nbSlot = s;
                return true;
            }
            return false;
        }

        // Tiles can play appear/disappear animations; a tile that's faded out
        // or deactivated shouldn't be stood on, hopped to, or spawned on.
        public static bool FloorVisible(scrFloor f)
        {
            return f != null && FloorVisible(f, f.gameObject, f.transform);
        }

        // Same test with the tile's GameObject and Transform handed in: both
        // are native property fetches, and the surface pass already holds them.
        private static bool FloorVisible(scrFloor f, GameObject go, Transform t)
        {
            return go.activeInHierarchy
                && f.opacity > 0.1f
                && Mathf.Abs(t.lossyScale.x) > 0.05f;
        }

        // ---------------- landing queries (falling pets) ----------------

        // A falling pet crossed a tile's top surface this frame? Swept against
        // the surface height at BOTH endpoints of the pet's motion: on a
        // slanted slab the surface height changes with x faster than the pet
        // falls, so testing only the new x lets pets phase straight through.
        // The prev endpoint is judged against the surface where it WAS last
        // frame (FDx/FDy): a scrolling camera moves tiles further per frame
        // than pets fall, and sweeping only the pet's motion tunnels too.
        public static bool TryLandOnFloor(float px, float prevX, float prevBottom,
            float newBottom, float petHalf, out Transform floor, out int floorIndex,
            out float offset)
        {
            for (int i = 0; i < _floorCount; i++)
            {
                float top = Fy[i] + FTopOff[i];
                float cx = Fx[i] + FCxOff[i];
                if (Mathf.Abs(px - cx) > FHalf[i] + petHalf * 0.3f) continue;
                float topPrev = top - FDy[i];
                // rotated tiles: the flat AABB top hangs in the air over most
                // of the slab — the hull's real surface height wins. Probes are
                // clamped into the hull span so a pet can still catch the edge
                // with feet slightly overhanging, like on flat tiles.
                if (FHn[i] > 0)
                {
                    float lo = cx - FHalf[i] + 0.5f, hi = cx + FHalf[i] - 0.5f;
                    float exact = HullTopAt(i, Mathf.Clamp(px, lo, hi));
                    if (float.IsNegativeInfinity(exact)) continue; // degenerate hull
                    top = exact;
                    // last frame the surface sat FDx/FDy back from where the
                    // hull is now: probe the current hull shifted accordingly
                    float exactPrev = HullTopAt(i, Mathf.Clamp(prevX + FDx[i], lo, hi));
                    topPrev = (float.IsNegativeInfinity(exactPrev) ? exact : exactPrev) - FDy[i];
                }
                if (prevBottom >= topPrev - 4f && newBottom <= top)
                {
                    floor = Ft[i];
                    floorIndex = Fidx[i];
                    offset = Mathf.Clamp(px - Fx[i],
                        FCxOff[i] - FHalf[i], FCxOff[i] + FHalf[i]);
                    return true;
                }
            }
            floor = null;
            floorIndex = -1;
            offset = 0f;
            return false;
        }

        // World-space union of every currently-visible renderer in the set.
        // False = nothing is drawing, so there is no surface.
        public static bool CombinedBounds(Renderer[] rends, out Bounds bnd)
        {
            bnd = default;
            bool any = false;
            for (int i = 0; i < rends.Length; i++)
            {
                var r = rends[i];
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                var sr = r as SpriteRenderer;
                if (sr != null && sr.sprite == null) continue;
                if (!any) { bnd = r.bounds; any = true; }
                else bnd.Encapsulate(r.bounds);
            }
            return any;
        }

        // Decoration-level visibility: fades from level events go through the
        // decoration's opacity (rendered by a custom shader), not the
        // SpriteRenderer's color — an event-faded deco can sit at color.a == 1
        // while being fully invisible on screen.
        public static bool DecoOpaque(scrDecoration d)
        {
            return d != null && !d.forceHide && d.opacity > 0.05f
                && d.gameObject.activeInHierarchy;
        }

        // A falling pet crossed a decoration's top edge this frame?
        public static bool TryLandOnDeco(float px, float prevBottom, float newBottom,
            float petHalf, out Transform deco, out float offset)
        {
            for (int i = 0; i < _decoCount; i++)
            {
                if (Mathf.Abs(px - Dx[i]) > DHalf[i] + petHalf * 0.3f) continue;
                // prev endpoint vs the top where it was LAST frame (DDy), so a
                // scrolling deco can't out-run the sweep and tunnel the pet
                if (prevBottom >= DTop[i] - DDy[i] - 4f && newBottom <= DTop[i])
                {
                    deco = Dt[i];
                    offset = Mathf.Clamp(px - Dx[i], -DHalf[i], DHalf[i]);
                    return true;
                }
            }
            deco = null;
            offset = 0f;
            return false;
        }

        // Fell into a planet's circle?
        public static bool TryLandOnPlanet(float px, float py, float petHalf,
            out Transform planet, out float angle)
        {
            if (!RuntimeContext.Settings.RidePlanets) { planet = null; angle = 0f; return false; }
            for (int i = 0; i < _planetCount; i++)
            {
                float r = PlR[i] + petHalf * 0.7f;
                float dx = px - Plx[i], dy = py - Ply[i];
                if (dx * dx + dy * dy <= r * r)
                {
                    planet = Plt[i];
                    angle = Mathf.Atan2(dy, dx);
                    return true;
                }
            }
            planet = null;
            angle = 0f;
            return false;
        }

        // A fast-moving planet plowed into the pet? Returns the fling velocity
        // (planet momentum plus a radial shove). `ignore` = the planet the pet
        // is riding, which is transport, not a collision.
        public static bool TryPlanetHit(float px, float py, float petHalf,
            Transform ignore, out Vector2 fling)
        {
            // only planets already gated as "moving fast" (~1.5 units/s in
            // world terms, so camera zoom doesn't decide what counts as a hit)
            fling = Vector2.zero;
            var p = new Vector2(px, py);

            for (int k = 0; k < _planetMoverCount; k++)
            {
                int i = PlMover[k];
                // riding any part of this planet = transport, not a collision
                if (ignore != null && (ReferenceEquals(Plt[i], ignore)
                    || ReferenceEquals(PlRootT[i], ignore)
                    || ignore.IsChildOf(PlRootT[i]))) continue;
                float r = PlR[i] * 1.2f + petHalf;
                var pv = new Vector2(PlVx[i], PlVy[i]);

                // swept test: distance to the segment the planet traveled this
                // frame, so fast swings can't tunnel through the pet
                var b = new Vector2(Plx[i], Ply[i]);
                if (!SweptCollision.SegmentIntersectsCircle(
                    PlSegX[i], PlSegY[i], b.x, b.y, p.x, p.y, r,
                    out _, out _)) continue;

                var away = p - b;
                float m = away.magnitude;
                away = m > 0.01f ? away / m : Vector2.up;
                // full planet momentum plus a shove — this is a home run, not a nudge
                fling = Vector2.ClampMagnitude(pv * 1.6f + away * 500f, 8000f);
                return true;
            }
            return false;
        }

        // A fast planet on course to reach the pet within ~a third of a
        // second? Same speed gate and inflated radius as TryPlanetHit, but
        // the swept segment points FORWARD along the planet's velocity —
        // enough warning to flash the panic face before the impact lands.
        // `threat` is the closest point of the scariest planet's path, so the
        // pet can work out which way to run rather than merely turning around.
        public static bool PlanetIncoming(float px, float py, float petHalf,
            Transform ignore, out Vector2 threat)
        {
            const float Lookahead = 0.22f; // seconds of warning
            // panic radius is a taste setting: 0 = only the ones about to
            // connect, higher = pets spook from further out
            float radiusMul = Mathf.Max(0f, RuntimeContext.Settings.PanicRadius);
            var p = new Vector2(px, py);

            threat = Vector2.zero;
            float bestDist2 = float.PositiveInfinity;

            for (int k = 0; k < _planetMoverCount; k++)
            {
                int i = PlMover[k];
                if (ignore != null && (ReferenceEquals(Plt[i], ignore)
                    || ReferenceEquals(PlRootT[i], ignore)
                    || ignore.IsChildOf(PlRootT[i]))) continue;
                var pv = new Vector2(PlVx[i], PlVy[i]);
                // tighter than the hit radius on purpose: a near miss is only
                // scary once it's genuinely close, not a screen away
                float r = (PlR[i] * 0.85f + petHalf * 0.5f) * radiusMul;

                var a = new Vector2(Plx[i], Ply[i]);
                Vector2 future = a + pv * Lookahead;
                SweptCollision.SegmentIntersectsCircle(
                    a.x, a.y, future.x, future.y, p.x, p.y, r,
                    out float closestX, out float closestY);
                var closest = new Vector2(closestX, closestY);
                float d2 = (p - closest).sqrMagnitude;
                // nearest threat wins: with several planets in flight, run from
                // the one actually breathing down the pet's neck
                if (d2 <= r * r && d2 < bestDist2)
                {
                    bestDist2 = d2;
                    threat = closest;
                }
            }
            return bestDist2 < float.PositiveInfinity;
        }

        // Screen-x of the nearest tile surface below a point, for the
        // terminal-velocity steer in PetActor.TickFall. Sideways distance is
        // weighted heavier than vertical: a tile almost straight down is a far
        // better aim than one the same distance off to the side.
        public static bool NearestFloorBelow(float px, float py, out float targetX)
        {
            targetX = 0f;
            float best = float.PositiveInfinity;
            for (int i = 0; i < _floorCount; i++)
            {
                float top = Fy[i] + FTopOff[i];
                if (top > py) continue; // above us — can't fall onto it
                float cx = Fx[i] + FCxOff[i];
                float dx = px - cx, dy = py - top;
                float cost = dx * dx + dy * dy * 0.25f;
                if (cost >= best) continue;
                best = cost;
                targetX = cx;
            }
            return !float.IsPositiveInfinity(best);
        }

        // ---------------- leap-target picks (walking pets) ----------------

        public static bool RandomFloor(out Transform t, out int idx)
        {
            if (_floorCount == 0) { t = null; idx = -1; return false; }
            int i = Random.Range(0, _floorCount);
            t = Ft[i]; idx = Fidx[i];
            return t != null;
        }

        public static bool RandomDeco(out Transform t)
        {
            if (_decoCount == 0) { t = null; return false; }
            t = Dt[Random.Range(0, _decoCount)];
            return t != null;
        }

        public static bool RandomPlanet(out Transform t)
        {
            t = null;
            if (_planetCount == 0 || !RuntimeContext.Settings.RidePlanets) return false;

            // one planet can hold two entries (ball + renderer); pick per
            // planet so a two-entry planet isn't twice as likely to be chosen
            int distinct = 0;
            for (int i = 0; i < _planetCount; i++)
                if (FirstEntryOfRoot(i)) distinct++;
            if (distinct == 0) return false;

            int pick = Random.Range(0, distinct);
            for (int i = 0; i < _planetCount; i++)
            {
                if (!FirstEntryOfRoot(i)) continue;
                if (pick-- > 0) continue;
                t = Plt[i];
                return t != null;
            }
            return false;
        }

        private static bool FirstEntryOfRoot(int i)
        {
            for (int j = 0; j < i; j++)
                if (PlRootT[j] == PlRootT[i]) return false;
            return true;
        }

        // X position above a random visible surface, for respawning from the top
        public static bool TryGetSpawnX(out float x)
        {
            if (_floorCount > 0) { x = Fx[Random.Range(0, _floorCount)]; return true; }
            if (_planetCount > 0) { x = Plx[Random.Range(0, _planetCount)]; return true; }
            if (_decoCount > 0) { x = Dx[Random.Range(0, _decoCount)]; return true; }
            x = 0f;
            return false;
        }

        // Count changed: drop extras / add newcomers. Survivors keep doing
        // whatever they were doing — dragging the count slider must not reset
        // the whole flock on every notch.
        private void ResizeFlock(int count)
        {
            _pets.RemoveAll(p => p == null);

            while (_pets.Count > count)
            {
                var pet = _pets[_pets.Count - 1];
                _pets.RemoveAt(_pets.Count - 1);
                Destroy(pet.gameObject);
            }

            while (_pets.Count < count)
            {
                var go = new GameObject("Shimeji_" + _pets.Count);
                go.transform.SetParent(_canvas.transform, false);
                go.AddComponent<Image>();
                var pet = go.AddComponent<PetActor>();
                pet.Init(_pets.Count);
                _pets.Add(pet);
            }
        }
    }
}
