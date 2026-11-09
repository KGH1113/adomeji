using System;
using System.Reflection;
using Adomeji.Pets.Rendering;
using Adomeji.Pets.Runtime;
using Adomeji.Shared.Diagnostics;
using HarmonyLib;
using UnityModManagerNet;

namespace Adomeji.Integrations.TUFReplay;

// Optional bridge to TUFReplay's ReplayApi. Adomeji has no reference to
// TUFReplay.dll: it finds TUFReplay.ReplayApi by name in the loaded assembly
// named TUFReplay and registers a provider object of its own. Either mod keeps
// working when the other is missing or turned off.
//
// TUFReplay may load after Adomeji. Until it shows up, the bridge tries again
// each time another mod finishes loading.
internal sealed class TUFReplayBridge : IDisposable
{
  private const string ProviderId = "Adomeji";
  private const int RequiredApiVersion = 1;
  private const string HarmonyId = "com.impl.adomeji.tufreplay";

  // The bridge waiting for TUFReplay; read by the static Harmony postfix.
  private static TUFReplayBridge _waiting;

  private readonly PetClock _clock;
  private readonly IPetVideoCapture _capture;
  private readonly IModLogger _logger;

  private Func<string, object, bool> _register;
  private Action<string> _unregister;
  private Func<int> _renderWidth;
  private Func<int> _renderHeight;
  private ReplayProvider _provider;
  private Harmony _harmony;
  private bool _connected;
  private bool _registered;

  public TUFReplayBridge(PetClock clock, IPetVideoCapture capture, IModLogger logger)
  {
    _clock = clock;
    _capture = capture;
    _logger = logger;
  }

  public void Connect()
  {
    if (_connected)
      return;

    _connected = true;
    try
    {
      if (!TryRegister())
        WaitForTUFReplay();
    }
    catch (Exception exception)
    {
      // optional: pets work without TUFReplay, so this must never block enabling
      _logger.Error("[TUFReplay] Connect", exception);
    }
  }

  public void Disconnect()
  {
    if (!_connected)
      return;

    _connected = false;
    StopWaiting();

    if (_registered)
    {
      _registered = false;
      try { _unregister(ProviderId); }
      catch (Exception exception) { _logger.Error("[TUFReplay] Unregister", exception); }
    }

    _provider?.Reset();
  }

  public void Dispose() => Disconnect();

  // True once TUFReplay was found, whether or not it took the provider: a
  // provider it refused is refused again, and TUFReplay logs why.
  private bool TryRegister()
  {
    if (!Resolve())
      return false;

    _provider ??= new ReplayProvider(_clock, _capture, _renderWidth, _renderHeight);
    try
    {
      _registered = _register(ProviderId, _provider);
    }
    catch (Exception exception)
    {
      _registered = false;
      _logger.Error("[TUFReplay] Register", exception);
    }

    if (_registered)
      _logger.Info("[TUFReplay] Pets follow TUFReplay renders.");
    return true;
  }

  private bool Resolve()
  {
    if (_register != null)
      return true;

    foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
    {
      if (assembly.GetName().Name != "TUFReplay")
        continue;

      Type api = assembly.GetType("TUFReplay.ReplayApi", false);
      if (api?.GetField("ApiVersion")?.GetValue(null) is not int version || version < RequiredApiVersion)
        continue;

      try
      {
        _register = Bind<Func<string, object, bool>>(api.GetMethod("Register"));
        _unregister = Bind<Action<string>>(api.GetMethod("Unregister"));
        _renderWidth = Bind<Func<int>>(api.GetProperty("RenderWidth")?.GetGetMethod());
        _renderHeight = Bind<Func<int>>(api.GetProperty("RenderHeight")?.GetGetMethod());
        return true;
      }
      catch (Exception exception)
      {
        _register = null;
        _logger.Error("[TUFReplay] TUFReplay " + version + " does not offer the Replay API Adomeji needs.", exception);
      }
    }

    return false;
  }

  private static T Bind<T>(MethodInfo method) where T : Delegate
  {
    if (method == null)
      throw new MissingMethodException("TUFReplay.ReplayApi", typeof(T).Name);
    return (T)Delegate.CreateDelegate(typeof(T), method);
  }

  private void WaitForTUFReplay()
  {
    _waiting = this;
    if (_harmony != null)
      return;

    MethodInfo load = AccessTools.Method(typeof(UnityModManager.ModEntry), "Load");
    if (load == null)
    {
      _logger.Error("[TUFReplay] UnityModManager.ModEntry.Load was not found, so Adomeji can't tell when TUFReplay loads. Renders show pets on real time.");
      return;
    }

    try
    {
      _harmony = new Harmony(HarmonyId);
      _harmony.Patch(load, postfix: new HarmonyMethod(typeof(TUFReplayBridge), nameof(AfterModLoaded)));
    }
    catch (Exception exception)
    {
      _harmony = null;
      _logger.Error("[TUFReplay] Harmony UnityModManager.ModEntry.Load", exception);
    }
  }

  private void StopWaiting()
  {
    if (_waiting == this)
      _waiting = null;
    _harmony?.UnpatchAll(HarmonyId);
    _harmony = null;
  }

  // The postfix stays in place until Adomeji turns off; once TUFReplay is
  // found it does nothing. Unpatching from inside the patched call is avoided.
  private static void AfterModLoaded()
  {
    TUFReplayBridge bridge = _waiting;
    if (bridge == null)
      return;

    try
    {
      if (bridge.TryRegister())
        _waiting = null;
    }
    catch (Exception exception)
    {
      bridge._logger.Error("[TUFReplay] " + nameof(AfterModLoaded), exception);
    }
  }
}
