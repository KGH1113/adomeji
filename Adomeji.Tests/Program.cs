using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using Adomeji.Gameplay.Events;
using Adomeji.Pets.Physics;
using Adomeji.Pets.Rendering;
using Adomeji.Pets.Runtime;
using Adomeji.Settings;
using Adomeji.Shared.Diagnostics;
using Adomeji.Shared.Text;
using Adomeji.Sprites.Formats;
using Adomeji.Sprites.Configuration;
using Adomeji.Sprites.Selection;
using Adomeji.UI.Input;
using Adomeji.UI;
using UnityEngine;
using UnityEngine.UI;

internal static class Program
{
  private static int _tests;

  private static int Main()
  {
    try
    {
      TestJudgements();
      TestWeightedSelection();
      TestNaturalSort();
      TestActionsXml();
      TestSpriteRoles();
      TestSettingsNormalization();
      TestSettingsReload();
      TestFrameSequence();
      TestPortalMath();
      TestHullTop();
      TestSweptCollision();
      TestPetRuntimeLifecycle();
      TestSimulationFrameDelta();
      TestPetClock();
      TestSettingsHotkeys();
      TestSettingsWindowLifecycle();
      Console.WriteLine($"Adomeji.Tests: {_tests} assertions passed.");
      return 0;
    }
    catch (Exception exception)
    {
      Console.Error.WriteLine(exception.Message);
      return 1;
    }
  }

  private static void TestJudgements()
  {
    Equal(JudgementKind.Perfect, JudgementMapper.FromName("Perfect"), "perfect mapping");
    Equal(JudgementKind.OverPress, JudgementMapper.FromName("OverPress"), "overpress mapping");
    Equal(JudgementKind.Unknown, JudgementMapper.FromName("FutureMargin"), "unknown mapping");
    Equal(JudgementKind.Perfect, JudgementMapper.FromName("XPerfect"), "r150 xperfect mapping");
    Equal(JudgementKind.Perfect, JudgementMapper.FromName("PerfectPlus"), "r150 perfect+ mapping");
    Equal(JudgementKind.Auto, JudgementMapper.FromName("Midspin"), "r150 midspin ignored");

    var tracker = new RunJudgementTracker();
    tracker.Record(JudgementKind.Auto);
    False(tracker.IsPurePerfect, "auto does not count as PP");
    tracker.Record(JudgementKind.Perfect);
    True(tracker.IsPurePerfect, "all counted hits perfect");
    tracker.Record(JudgementKind.TooLate);
    False(tracker.IsPurePerfect, "non-perfect breaks PP");
    tracker.Reset();
    False(tracker.IsPurePerfect, "empty run is not PP");
  }

  private static void TestWeightedSelection()
  {
    float[] weights = { 7f, 3f, 0f };
    Equal(0, WeightedIndexSelector.Select(3, i => weights[i], 0.69f, 2), "weighted lower range");
    Equal(1, WeightedIndexSelector.Select(3, i => weights[i], 0.71f, 2), "weighted upper range");
    Equal(1, WeightedIndexSelector.Select(3, _ => 0f, 0.5f, 1), "all-zero fallback");
    Equal(-1, WeightedIndexSelector.Select(0, _ => 1f, 0f, 0), "empty selection");
  }

  private static void TestNaturalSort()
  {
    var names = new List<string> { "walk10", "walk2", "walk1" };
    names.Sort(NaturalStringComparer.Compare);
    Equal("walk1,walk2,walk10", string.Join(",", names), "natural sprite ordering");
  }

  private static void TestActionsXml()
  {
    const string xml = "<ActionList><Action Name='Walk'><Pose Image='/shime1.png'/><Pose Image=\"img\\Mee\\shime2.png\" ImageAnchor='ignored'/></Action><Action Name='Walk'/></ActionList>";
    Dictionary<string, string[]> actions = ActionsXmlParser.Parse(xml);
    True(actions.ContainsKey("Walk"), "actions.xml action found");
    Equal("shime1,shime2", string.Join(",", actions["Walk"]), "actions.xml pose order");
  }

  private static void TestSettingsNormalization()
  {
    var settings = new AdomejiSettings
    {
      Count = 999,
      Scale = -1f,
      Speed = 99f,
      SpritePacks = new List<string> { "", null },
      PackWeights = new List<float> { -2f, 4f, 8f },
      AccentHex = " ",
      ToggleKey = null,
    };
    settings.Normalize();
    Equal(8, settings.Count, "normal pet cap");
    Equal(0.5f, settings.Scale, "minimum scale");
    Equal(2.5f, settings.Speed, "maximum speed");
    Equal("ELLIE", settings.SpritePacks[0], "default sprite pack");
    Equal(1, settings.PackWeights.Count, "weights match packs");
    Equal(1f, settings.PackWeights[0], "invalid weight repaired");
    Equal("FF6B52", settings.AccentHex, "default accent");
    Equal("F8", settings.ToggleKey, "default hotkey");
  }

  // UMM loads and saves settings with XmlSerializer, once per game launch.
  private static void TestSettingsReload()
  {
    AdomejiSettings Relaunch(AdomejiSettings saved)
    {
      var serializer = new XmlSerializer(typeof(AdomejiSettings));
      var file = new StringWriter();
      serializer.Serialize(file, saved);
      var loaded = (AdomejiSettings)serializer.Deserialize(new StringReader(file.ToString()));
      loaded.Normalize();
      return loaded;
    }

    var settings = new AdomejiSettings();
    settings.Normalize();
    for (int launch = 0; launch < 3; launch++) settings = Relaunch(settings);
    Equal(1, settings.SpritePacks.Count, "default pack is not added again on each launch");
    Equal(1, settings.PackWeights.Count, "default weight is not added again on each launch");

    settings = new AdomejiSettings { SpritePacks = { "Other" }, PackWeights = { 2f } };
    settings.Normalize();
    for (int launch = 0; launch < 3; launch++) settings = Relaunch(settings);
    Equal("Other", string.Join(",", settings.SpritePacks), "a removed default pack stays removed");
    Equal(2f, settings.PackWeights[0], "weight survives relaunch");

    // [Other, ELLIE] at [2, 5], after two launches of the old build
    var broken = new AdomejiSettings
    {
      SpritePacks = { "ELLIE", "ELLIE", "Other", "ELLIE" },
      PackWeights = { 1f, 1f, 2f, 5f },
    };
    broken.Normalize();
    Equal("Other,ELLIE", string.Join(",", broken.SpritePacks), "repeated packs keep their last entry");
    Equal("2,5", string.Join(",", broken.PackWeights), "repaired packs keep their own weights");

    var blanks = new AdomejiSettings
    {
      SpritePacks = { "A", "", "B" },
      PackWeights = { 3f, 9f, 4f },
    };
    blanks.Normalize();
    Equal("A,B", string.Join(",", blanks.SpritePacks), "blank packs dropped");
    Equal("3,4", string.Join(",", blanks.PackWeights), "weights stay beside their packs");
  }

  private static void TestFrameSequence()
  {
    string Frames(int count, bool pingPong, int ticks)
    {
      var frames = new List<string>();
      for (int tick = 0; tick < ticks; tick++) frames.Add(FrameSequence.At(tick, count, pingPong).ToString());
      return string.Join(" ", frames);
    }

    Equal("0 1 2 1 0 1 2 1 0", Frames(3, true, 9), "wave ping-pongs without doubled ends");
    Equal("0 1 2 0 1 2", Frames(3, false, 6), "loop restarts");
    Equal("0 1 2 3 4 3 2 1 0", Frames(5, true, 9), "five-frame ping-pong");
    Equal("0 1 0 1", Frames(2, true, 4), "two frames alternate");
    Equal("0 0", Frames(1, true, 2), "single frame");
  }

  private static void TestSpriteRoles()
  {
    bool Valid(string role) => role == "walk" || role == "disabled";
    True(SpriteRoleEntryParser.TryParse("walk10=walk", Valid, out SpriteRoleEntry role), "sprite role parsed");
    Equal(SpriteRoleEntryKind.Role, role.Kind, "sprite role kind");
    Equal("walk10", role.Key, "sprite role filename");
    True(SpriteRoleEntryParser.TryParse("pingpong=false", Valid, out SpriteRoleEntry pingPong), "ping-pong parsed");
    False(pingPong.Enabled, "ping-pong value");
    True(SpriteRoleEntryParser.TryParse("behavior.jump=false", Valid, out SpriteRoleEntry behavior), "behavior parsed");
    Equal("jump", behavior.Key, "behavior key");
    False(SpriteRoleEntryParser.TryParse("walk0=not-a-role", Valid, out _), "invalid role ignored");
    False(SpriteRoleEntryParser.TryParse("# comment", Valid, out _), "comment ignored");
  }

  private static void TestPortalMath()
  {
    Equal(103f, PortalMath.Wrap(-5f, 4f, 100f), "horizontal portal left to right");
    Equal(-3f, PortalMath.Wrap(105f, 4f, 100f), "horizontal portal right to left");
    Equal(50f, PortalMath.Wrap(50f, 4f, 100f), "portal keeps in-bounds position");
  }

  private static void TestHullTop()
  {
    float[] xs = { 0f, 10f, 10f, 0f };
    float[] ys = { 0f, 5f, 0f, 0f };
    Equal(2.5f, SurfaceGeometry.TopAt(xs, ys, 0, 4, 5f), "sloped hull upper envelope");
    True(float.IsNegativeInfinity(SurfaceGeometry.TopAt(xs, ys, 0, 4, 20f)), "outside hull");
  }

  private static void TestSweptCollision()
  {
    True(SweptCollision.SegmentIntersectsCircle(0f, 0f, 10f, 0f, 5f, 1f, 1.1f, out float x, out float y), "swept hit");
    Equal(5f, x, "swept closest x");
    Equal(0f, y, "swept closest y");
    False(SweptCollision.SegmentIntersectsCircle(0f, 0f, 10f, 0f, 5f, 3f, 1f, out _, out _), "swept miss");
  }

  private static void TestPetRuntimeLifecycle()
  {
    var factory = new FakeFlockFactory();
    var runtime = new PetRuntime(new NullLogger(), factory);

    runtime.Tick(0.1f);
    Equal(0, factory.CreateCount, "tick before start is ignored");
    runtime.Start();
    runtime.Start();
    Equal(1, factory.CreateCount, "duplicate start creates one flock");
    runtime.Tick(0.25f);
    Equal(1, factory.Flock.TickCount, "running tick forwarded");
    runtime.HandleJudgement(JudgementKind.Perfect);
    runtime.HandleLevelCleared(true);
    runtime.HandleLevelFailed();
    Equal(1, factory.Flock.JudgementCount, "judgement forwarded");
    Equal(1, factory.Flock.ClearCount, "clear forwarded");
    Equal(1, factory.Flock.DeathCount, "failure forwarded while active");
    runtime.Stop();
    runtime.Stop();
    Equal(1, factory.Flock.DisposeCount, "duplicate stop disposes once");
    runtime.HandleLevelFailed();
    Equal(1, factory.Flock.DeathCount, "failure ignored while stopped");
    runtime.Dispose();
    Equal(1, factory.Flock.DisposeCount, "dispose after stop is idempotent");
  }

  private static void TestSimulationFrameDelta()
  {
    Equal(0.016f, SimulationFrameDelta.Resolve(0f, 0.016f),
      "unscaled time continues when game time is paused");
    Equal(0.02f, SimulationFrameDelta.Resolve(0.02f, 0f),
      "callback delta is fallback when unscaled time is unavailable");
    Equal(0f, SimulationFrameDelta.Resolve(float.NaN, float.PositiveInfinity),
      "invalid frame deltas are rejected");
  }

  private static void TestPetClock()
  {
    var clock = new PetClock();
    True(clock.TryAdvance(0.016f, out float seconds), "real time advances outside renders");
    Equal(0.016f, seconds, "real time is used outside renders");

    clock.SetVideoTime(5000000);
    False(clock.FollowsVideo, "video time is ignored outside renders");

    clock.BeginRender();
    True(clock.TryAdvance(0.02f, out seconds), "real time until the render's clock starts");
    Equal(0.02f, seconds, "level loading keeps real time");

    clock.SetVideoTime(1000000);
    True(clock.FollowsVideo, "first video tick starts the video clock");
    False(clock.TryAdvance(0.25f, out seconds), "first video frame does not advance");

    clock.SetVideoTime(1166667);
    clock.SetVideoTime(1166667);
    True(clock.TryAdvance(0.25f, out seconds), "video frame advances pets");
    Near(1f / 60f, seconds, "pets advance by video time, not real time");
    False(clock.TryAdvance(0.25f, out seconds), "repeated video time skips the frame");

    clock.SetVideoTime(1000000);
    False(clock.TryAdvance(0.25f, out seconds), "video time never goes back");

    clock.SetVideoTime(1500000);
    clock.SetVideoTime(1833333);
    True(clock.TryAdvance(0.25f, out seconds), "frames missed by the tick are caught up");
    Near(1f / 15f, seconds, "caught-up time is the whole video span");

    clock.EndRender();
    False(clock.FollowsVideo, "render end returns to real time");
    True(clock.TryAdvance(0.016f, out seconds), "real time after the render");
    Equal(0.016f, seconds, "real time is used after the render");

    clock.BeginRender();
    False(clock.FollowsVideo, "a new render waits for its own clock");
  }

  private static void TestSettingsHotkeys()
  {
    var settings = new AdomejiSettings { ToggleKey = "F8" };
    var input = new FakeKeyInput();
    var hotkeys = new SettingsHotkeyController(settings, input);
    int toggles = 0;
    int hides = 0;

    input.Set(1, KeyCode.F8);
    hotkeys.Tick(false, () => toggles++, () => hides++);
    Equal(1, toggles, "configured hotkey toggles window");

    input.IsTextFieldFocused = true;
    input.Set(2, KeyCode.F8);
    hotkeys.Tick(true, () => toggles++, () => hides++);
    Equal(1, toggles, "focused input field suppresses toggle");
    input.IsTextFieldFocused = false;

    var label = new Text();
    hotkeys.BeginCapture(label);
    input.Set(3, KeyCode.Escape);
    hotkeys.Tick(true, () => toggles++, () => hides++);
    False(hotkeys.IsCapturing, "escape cancels key capture");
    Equal("F8", label.text, "cancel restores current key label");

    hotkeys.BeginCapture(label);
    input.Set(4, KeyCode.A);
    hotkeys.Tick(true, () => toggles++, () => hides++);
    Equal("A", settings.ToggleKey, "captured key stored");
    Equal(1, toggles, "capture press does not toggle");
    hotkeys.Tick(true, () => toggles++, () => hides++);
    Equal(1, toggles, "capture frame latch suppresses toggle");

    input.Set(5, KeyCode.A);
    hotkeys.Tick(true, () => toggles++, () => hides++);
    Equal(2, toggles, "captured key toggles on later frame");

    input.Set(6, KeyCode.Escape);
    hotkeys.Tick(true, () => toggles++, () => hides++);
    Equal(1, hides, "escape hides visible window");
  }

  private static void TestSettingsWindowLifecycle()
  {
    var lifecycle = new SettingsWindowLifecycle();
    Equal(WindowShowAction.Build, lifecycle.Show(), "first show builds view");
    Equal(WindowShowAction.None, lifecycle.Show(), "duplicate show is ignored");
    True(lifecycle.Hide(), "first hide changes visibility");
    False(lifecycle.Hide(), "duplicate hide is ignored");
    Equal(WindowShowAction.Activate, lifecycle.Show(), "second show reuses view");
    lifecycle.RequestRebuild();
    True(lifecycle.ConsumeVisibleRebuild(), "visible rebuild consumed once");
    False(lifecycle.ConsumeVisibleRebuild(), "rebuild request is edge-triggered");
    lifecycle.MarkViewDestroyed();
    lifecycle.Hide();
    Equal(WindowShowAction.Build, lifecycle.Show(), "destroyed view rebuilds on show");
    True(lifecycle.Dispose(), "first dispose succeeds");
    False(lifecycle.Dispose(), "duplicate dispose is ignored");
    bool threw = false;
    try { lifecycle.Show(); }
    catch (ObjectDisposedException) { threw = true; }
    True(threw, "show after dispose throws");
  }

  private static void True(bool value, string name)
  {
    _tests++;
    if (!value) throw new InvalidOperationException($"FAILED: {name}");
  }

  private static void False(bool value, string name) => True(!value, name);

  private static void Near(float expected, float actual, string name)
  {
    _tests++;
    if (Math.Abs(expected - actual) > 0.0001f)
      throw new InvalidOperationException($"FAILED: {name}; expected={expected}, actual={actual}");
  }

  private static void Equal<T>(T expected, T actual, string name)
  {
    _tests++;
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
      throw new InvalidOperationException($"FAILED: {name}; expected={expected}, actual={actual}");
  }

  private sealed class FakeFlockFactory : IPetFlockFactory
  {
    public readonly FakeFlock Flock = new FakeFlock();
    public int CreateCount;
    public IPetFlock Create() { CreateCount++; return Flock; }
  }

  private sealed class FakeFlock : IPetFlock
  {
    public int TickCount;
    public int JudgementCount;
    public int ClearCount;
    public int DeathCount;
    public int DisposeCount;
    public void Tick(float deltaTime) => TickCount++;
    public void NotifyJudgement(JudgementKind judgement) => JudgementCount++;
    public void NotifyClear(bool purePerfect) => ClearCount++;
    public void NotifyDeath() => DeathCount++;
    public void Dispose() => DisposeCount++;
  }

  private sealed class NullLogger : IModLogger
  {
    public void Info(string message) { }
    public void Error(string message) { }
    public void Error(string context, Exception exception) { }
  }

  private sealed class FakeKeyInput : IKeyInput
  {
    private KeyCode _pressed = KeyCode.None;
    public bool AnyKeyDown => _pressed != KeyCode.None;
    public int FrameCount { get; private set; }
    public bool IsTextFieldFocused { get; set; }
    public bool GetKeyDown(KeyCode key) => key == _pressed;
    public void Set(int frame, KeyCode pressed)
    {
      FrameCount = frame;
      _pressed = pressed;
    }
  }
}
