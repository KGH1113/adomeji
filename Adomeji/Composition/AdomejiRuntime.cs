using System;
using Adomeji.Gameplay.Events;
using Adomeji.Gameplay.Patches;
using Adomeji.Integrations.TUFReplay;
using Adomeji.Pets.Rendering;
using Adomeji.Pets.Runtime;
using Adomeji.Settings;
using Adomeji.Sprites.Catalog;
using Adomeji.Sprites.Loading;
using Adomeji.Shared.Diagnostics;
using Adomeji.UI;
using Adomeji.UI.Input;
using Adomeji.UI.Services;
using UnityModManagerNet;

namespace Adomeji.Composition;

internal sealed class AdomejiRuntime : IDisposable
{
  private readonly IModLogger _logger;
  private readonly IPetRuntime _pets;
  private readonly PetClock _clock;
  private readonly TUFReplayBridge _tufReplay;
  private readonly GameplayPatchInstaller _patches;
  private readonly ISettingsStore _settings;
  private readonly ISettingsWindow _settingsWindow;

  private bool _enabled;
  private bool _disposed;

  private AdomejiRuntime(
    IModLogger logger,
    IPetRuntime pets,
    PetClock clock,
    TUFReplayBridge tufReplay,
    GameplayPatchInstaller patches,
    ISettingsStore settings,
    ISettingsWindow settingsWindow
  )
  {
    _logger = logger;
    _pets = pets;
    _clock = clock;
    _tufReplay = tufReplay;
    _patches = patches;
    _settings = settings;
    _settingsWindow = settingsWindow;
  }

  public AdomejiSettings Settings => _settings.Current;

  public static AdomejiRuntime Create(
    UnityModManager.ModEntry modEntry
  )
  {
    IModLogger logger = new UmmModLogger(modEntry.Logger);
    ISettingsStore settings = SettingsStore.Load(modEntry);
    IPetRuntime pets = new PetRuntime(logger);
    IGameplayEventSink events = new GameplayEventSink(pets);
    var clock = new PetClock();
    var tufReplay = new TUFReplayBridge(clock, new PetFlockVideoCapture(), logger);

    var patches = new GameplayPatchInstaller(events, logger);
    RuntimeContext.Initialize(settings, logger, modEntry.Path, modEntry.Info.Version.ToString());
    ISpritePackCatalog catalog = new SpritePackCatalog();
    ISpritePackLoader loader = new SpritePackLoader();
    IUpdatePreferencesStore updatePreferences = UpdatePreferencesStore.Load(modEntry.Path);
    ISettingsWindow settingsWindow = new SettingsWindowController(
      settings,
      catalog,
      loader,
      new PetCustomizationService(),
      new SpriteRoleStore(),
      updatePreferences,
      new UnityKeyInput());

    return new AdomejiRuntime(logger, pets, clock, tufReplay, patches, settings, settingsWindow);
  }

  public void Tick(float deltaTime)
  {
    if (!_enabled || _disposed)
      return;

    if (_clock.TryAdvance(deltaTime, out float petSeconds))
      _pets.Tick(petSeconds);
    _settingsWindow.Tick();
  }

  public void ShowSettings()
  {
    ThrowIfDisposed();
    _settingsWindow.Show();
  }

  public void SaveSettings()
  {
    ThrowIfDisposed();
    _settings.Save();
  }

  public void SetEnabled(bool enabled)
  {
    ThrowIfDisposed();

    if (enabled)
      Enable();
    else
      Disable();
  }

  private void Enable()
  {
    if (_enabled)
      return;

    _patches.Install();

    try
    {
      _pets.Start();
      _tufReplay.Connect();
      _enabled = true;
      _logger.Info("Adomeji enabled.");
    }
    catch
    {
      _patches.Uninstall();
      throw;
    }
  }

  private void Disable()
  {
    if (!_enabled)
      return;

    _enabled = false;

    _patches.Uninstall();
    _tufReplay.Disconnect();
    _pets.Stop();
    _settingsWindow.Hide();
    _settings.Save();

    _logger.Info("Adomeji disabled.");
  }

  public void Dispose()
  {
    if (_disposed)
      return;

    Disable();

    _patches.Dispose();
    _tufReplay.Dispose();
    _pets.Dispose();
    _settingsWindow.Dispose();
    RuntimeContext.Clear();

    _disposed = true;
  }

  private void ThrowIfDisposed()
  {
    if (_disposed)
      throw new ObjectDisposedException(nameof(AdomejiRuntime));
  }
}
