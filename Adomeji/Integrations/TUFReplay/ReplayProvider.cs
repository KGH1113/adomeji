using System;
using Adomeji.Pets.Rendering;
using Adomeji.Pets.Runtime;
using UnityEngine;

namespace Adomeji.Integrations.TUFReplay;

// What TUFReplay calls during its replays. TUFReplay finds these public
// methods by name, so their names and signatures are the contract; see
// TUFReplay's docs/replay-api.md.
//
// In-game replays need nothing from here: the game plays the run in real time
// and pets react to its judgements as usual. Renders are different: the game
// runs slower than the video, and the renderer draws the pet canvas into a
// video of its own size, so pets follow the video's clock and space.
internal sealed class ReplayProvider
{
  private readonly PetClock _clock;
  private readonly IPetVideoCapture _capture;
  private readonly Func<int> _renderWidth;
  private readonly Func<int> _renderHeight;

  public ReplayProvider(
    PetClock clock,
    IPetVideoCapture capture,
    Func<int> renderWidth,
    Func<int> renderHeight)
  {
    _clock = clock;
    _capture = capture;
    _renderWidth = renderWidth;
    _renderHeight = renderHeight;
  }

  public void OnReplayBegin(bool render)
  {
    if (render)
      _clock.BeginRender();
  }

  public void OnReplayClock(long ticks) => _clock.SetVideoTime(ticks);

  // Called once, as the video starts; the video's size is final from here on.
  public Canvas GetReplayCanvases() => _capture.Begin(_renderWidth(), _renderHeight());

  public void OnReplayEnd() => Reset();

  // Also used when Adomeji turns off mid-replay: TUFReplay stops calling an
  // unregistered provider, so no OnReplayEnd follows.
  internal void Reset()
  {
    _clock.EndRender();
    _capture.End();
  }
}
