using Adomeji.Pets.Surfaces;
using UnityEngine;

namespace Adomeji.Pets.Rendering;

// The pets' side of being drawn into a video by a renderer.
internal interface IPetVideoCapture
{
  // Hands the pet canvas to the renderer, which draws it into a video of
  // this size. Null when no pets are out.
  Canvas Begin(int width, int height);

  // Takes the canvas back once the renderer has restored it.
  void End();
}

internal sealed class PetFlockVideoCapture : IPetVideoCapture
{
  public Canvas Begin(int width, int height) => PetFlockController.BeginVideoCapture(width, height);
  public void End() => PetFlockController.EndVideoCapture();
}
