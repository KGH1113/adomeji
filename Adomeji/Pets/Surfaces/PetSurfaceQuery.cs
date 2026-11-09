using UnityEngine;

namespace Adomeji.Pets.Surfaces;

internal sealed class PetSurfaceQuery : IPetSurfaceQuery
{
  public static readonly IPetSurfaceQuery Shared = new PetSurfaceQuery();
  private PetSurfaceQuery() { }

  public bool AnyPlanetMoving => PetFlockController.AnyPlanetMoving;
  public bool NoFloorActive => PetFlockController.NoFloorActive;
  public bool NoWallActive => PetFlockController.NoWallActive;
  public bool NoCeilingActive => PetFlockController.NoCeilingActive;
  public float ZoomFactor => PetFlockController.ZoomFactor;
  public float PixelsPerUnit => PetFlockController.PixelsPerUnit;
  public float DisplayScaleX => PetFlockController.DisplayScaleX;
  public float SpaceWidth => PetFlockController.SpaceWidth;
  public float SpaceHeight => PetFlockController.SpaceHeight;
  public float DisplayMapX(float x, float anchorX) => PetFlockController.DisplayMapX(x, anchorX);
  public float DisplayMapY(float y, float anchorY) => PetFlockController.DisplayMapY(y, anchorY);
  public Camera GameCamera() => PetFlockController.GameCamera();
  public int FloorSlot(int listIndex, Transform transform) => PetFlockController.FloorSlot(listIndex, transform);
  public int PlanetSlot(Transform transform) => PetFlockController.PlanetSlot(transform);
  public int DecoSlot(Transform transform) => PetFlockController.DecoSlot(transform);
  public Vector2 FloorScreenPos(int slot) => PetFlockController.FloorScreenPos(slot);
  public Vector2 PlanetScreenPos(int slot) => PetFlockController.PlanetScreenPos(slot);
  public Vector2 DecoScreenPos(int slot) => PetFlockController.DecoScreenPos(slot);
  public float PlanetRadius(int slot) => PetFlockController.PlanetRadius(slot);
  public float DecoHalfWidth(int slot) => PetFlockController.DecoHalfWidth(slot);
  public scrFloor FloorOf(int slot) => PetFlockController.FloorOf(slot);
  public Transform FloorTransform(int slot) => PetFlockController.FloorTransform(slot);
  public int FloorIndexOf(int slot) => PetFlockController.FloorIndexOf(slot);
  public bool FloorSurfaceTopAt(int slot, float screenX, out float topScreenY) =>
    PetFlockController.FloorSurfaceTopAt(slot, screenX, out topScreenY);
  public void FloorMetrics(int slot, out float halfPx, out float topOff, out float centerOff) =>
    PetFlockController.FloorMetrics(slot, out halfPx, out topOff, out centerOff);
  public bool NeighborSlot(int slot, int side, out Vector2 neighbor, out int neighborSlot) =>
    PetFlockController.NeighborSlot(slot, side, out neighbor, out neighborSlot);
  public bool FloorVisible(scrFloor floor) => PetFlockController.FloorVisible(floor);
  public bool CombinedBounds(Renderer[] renderers, out Bounds bounds) =>
    PetFlockController.CombinedBounds(renderers, out bounds);
  public bool DecoOpaque(scrDecoration decoration) => PetFlockController.DecoOpaque(decoration);
  public bool TryLandOnFloor(float x, float previousX, float previousBottom, float newBottom,
    float petHalf, out Transform floor, out int floorIndex, out float offset) =>
    PetFlockController.TryLandOnFloor(x, previousX, previousBottom, newBottom, petHalf,
      out floor, out floorIndex, out offset);
  public bool TryLandOnDeco(float x, float previousBottom, float newBottom, float petHalf,
    out Transform decoration, out float offset) =>
    PetFlockController.TryLandOnDeco(x, previousBottom, newBottom, petHalf, out decoration, out offset);
  public bool TryLandOnPlanet(float x, float y, float petHalf, out Transform planet, out float angle) =>
    PetFlockController.TryLandOnPlanet(x, y, petHalf, out planet, out angle);
  public bool TryPlanetHit(float x, float y, float petHalf, Transform ignore, out Vector2 fling) =>
    PetFlockController.TryPlanetHit(x, y, petHalf, ignore, out fling);
  public bool PlanetIncoming(float x, float y, float petHalf, Transform ignore, out Vector2 threat) =>
    PetFlockController.PlanetIncoming(x, y, petHalf, ignore, out threat);
  public bool NearestFloorBelow(float x, float y, out float targetX) =>
    PetFlockController.NearestFloorBelow(x, y, out targetX);
  public bool RandomFloor(out Transform transform, out int index) =>
    PetFlockController.RandomFloor(out transform, out index);
  public bool RandomDeco(out Transform transform) => PetFlockController.RandomDeco(out transform);
  public bool RandomPlanet(out Transform transform) => PetFlockController.RandomPlanet(out transform);
  public bool TryGetSpawnX(out float x) => PetFlockController.TryGetSpawnX(out x);
}
