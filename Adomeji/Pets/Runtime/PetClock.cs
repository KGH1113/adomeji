namespace Adomeji.Pets.Runtime;

// The time pets advance by each frame. Real time, except while a TUFReplay
// render runs: the game then plays slower than the video it makes, so real
// time would send pets rushing through the video. Renders hand in the video's
// own clock instead, and pets advance exactly as far as the video does.
internal sealed class PetClock
{
  private const double TicksPerSecond = 10000000d; // TimeSpan.TicksPerSecond

  private bool _rendering;
  private bool _videoStarted;
  private long _videoTicks; // the latest video time handed in
  private long _usedTicks;  // the video time pets have advanced to

  // A render's clock has started; until then, pets keep real time.
  public bool FollowsVideo => _rendering && _videoStarted;

  public void BeginRender()
  {
    _rendering = true;
    _videoStarted = false;
    _videoTicks = 0;
    _usedTicks = 0;
  }

  // The video time now, in 100 ns ticks. Ignored outside a render; never goes back.
  public void SetVideoTime(long ticks)
  {
    if (!_rendering)
      return;

    if (!_videoStarted)
    {
      _videoStarted = true;
      _videoTicks = ticks;
      _usedTicks = ticks;
      return;
    }

    if (ticks > _videoTicks)
      _videoTicks = ticks;
  }

  public void EndRender()
  {
    _rendering = false;
    _videoStarted = false;
  }

  // The seconds pets advance this frame. False on a render frame where the
  // video did not move on, so pets skip the frame instead of re-running it.
  public bool TryAdvance(float realSeconds, out float seconds)
  {
    if (!FollowsVideo)
    {
      seconds = realSeconds;
      return true;
    }

    long elapsed = _videoTicks - _usedTicks;
    _usedTicks = _videoTicks;
    seconds = (float)(elapsed / TicksPerSecond);
    return elapsed > 0;
  }
}
