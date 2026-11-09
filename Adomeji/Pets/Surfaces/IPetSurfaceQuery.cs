using UnityEngine;

namespace Adomeji.Pets.Surfaces;

internal interface IPetSurfaceQuery
{
  bool AnyPlanetMoving { get; }
  bool NoFloorActive { get; }
  bool NoWallActive { get; }
  bool NoCeilingActive { get; }
  float ZoomFactor { get; }
  float PixelsPerUnit { get; }
  float DisplayScaleX { get; }
  float SpaceWidth { get; }
  float SpaceHeight { get; }

  float DisplayMapX(float x, float anchorX);
  float DisplayMapY(float y, float anchorY);
  Camera GameCamera();
  int FloorSlot(int listIndex, Transform transform);
  int PlanetSlot(Transform transform);
  int DecoSlot(Transform transform);
  Vector2 FloorScreenPos(int slot);
  Vector2 PlanetScreenPos(int slot);
  Vector2 DecoScreenPos(int slot);
  float PlanetRadius(int slot);
  float DecoHalfWidth(int slot);
  scrFloor FloorOf(int slot);
  Transform FloorTransform(int slot);
  int FloorIndexOf(int slot);
  bool FloorSurfaceTopAt(int slot, float screenX, out float topScreenY);
  void FloorMetrics(int slot, out float halfPx, out float topOff, out float centerOff);
  bool NeighborSlot(int slot, int side, out Vector2 neighbor, out int neighborSlot);
  bool FloorVisible(scrFloor floor);
  bool CombinedBounds(Renderer[] renderers, out Bounds bounds);
  bool DecoOpaque(scrDecoration decoration);
  bool TryLandOnFloor(float x, float previousX, float previousBottom, float newBottom,
    float petHalf, out Transform floor, out int floorIndex, out float offset);
  bool TryLandOnDeco(float x, float previousBottom, float newBottom, float petHalf,
    out Transform decoration, out float offset);
  bool TryLandOnPlanet(float x, float y, float petHalf, out Transform planet, out float angle);
  bool TryPlanetHit(float x, float y, float petHalf, Transform ignore, out Vector2 fling);
  bool PlanetIncoming(float x, float y, float petHalf, Transform ignore, out Vector2 threat);
  bool NearestFloorBelow(float x, float y, out float targetX);
  bool RandomFloor(out Transform transform, out int index);
  bool RandomDeco(out Transform transform);
  bool RandomPlanet(out Transform transform);
  bool TryGetSpawnX(out float x);
}
