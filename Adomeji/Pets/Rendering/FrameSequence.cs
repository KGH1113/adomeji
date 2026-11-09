namespace Adomeji.Pets.Rendering;

// Which frame of a repeating animation shows at a given tick.
internal static class FrameSequence
{
  // Loop:      0 1 2 | 0 1 2 | ...
  // Ping-pong: 0 1 2 1 | 0 1 2 1 | ... — the turning frames are not
  //            doubled, so the motion never stalls at either end.
  public static int At(int tick, int count, bool pingPong)
  {
    if (count <= 1 || tick < 0)
      return 0;
    if (!pingPong || count < 3)
      return tick % count;

    int cycle = count * 2 - 2;
    int step = tick % cycle;
    return step < count ? step : cycle - step;
  }
}
