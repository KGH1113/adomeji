using System;
using System.Collections.Generic;
using Adomeji.Sprites.Loading;
using UnityModManagerNet;

namespace Adomeji.Settings;

public sealed class AdomejiSettings : UnityModManager.ModSettings
{
  public const int CurrentSchemaVersion = 1;

  public int SchemaVersion = CurrentSchemaVersion;
  public int Count = 3;
  public bool RaisePetLimit;
  public float Scale = 1f;
  public bool UncapScale;
  public float Speed = 1f;
  public bool FallSteer = true;
  public float FallSteerScale = 1f;
  public bool AllowDrag = true;
  public bool OptimizedMode;
  // Start empty: UMM loads settings with XmlSerializer, which appends the
  // saved entries to whatever the field already holds. Normalize adds the
  // default pack when nothing is selected.
  public List<string> SpritePacks = new List<string>();
  public List<float> PackWeights = new List<float>();
  public bool FilterPets = true;
  public bool SwallowMouse = true;
  public bool NoFloor;
  public bool NoWall;
  public bool NoCeiling;
  public bool WrapFloor = true;
  public bool WrapWall = true;
  public bool WrapCeiling = true;
  public bool RidePlanets = true;
  public float PanicRadius = 1f;
  public bool PetCollisionsTiles = true;
  public bool PetCollisionsScreen = true;
  public bool ZoomScale = true;
  public bool ShowHitboxes;
  public float UiScale = 1f;
  public string AccentHex = "FF6B52";
  public string ToggleKey = "F8";

  public int MaxPets => RaisePetLimit ? 100 : 8;
  public float MaxScale => UncapScale ? 20f : 3f;

  public void Normalize()
  {
    Count = UnityEngine.Mathf.Clamp(Count, 1, MaxPets);
    Scale = UnityEngine.Mathf.Clamp(Scale, 0.5f, MaxScale);
    Speed = UnityEngine.Mathf.Clamp(Speed, 0.5f, 2.5f);
    FallSteerScale = UnityEngine.Mathf.Clamp(FallSteerScale, 0.1f, 3f);
    PanicRadius = UnityEngine.Mathf.Clamp(PanicRadius, 0f, 4f);
    UiScale = UnityEngine.Mathf.Clamp(UiScale, 0.6f, 1.6f);
    AccentHex = string.IsNullOrWhiteSpace(AccentHex) ? "FF6B52" : AccentHex;
    ToggleKey = string.IsNullOrWhiteSpace(ToggleKey) ? "F8" : ToggleKey;

    NormalizePacks();
    WrapCeiling = WrapFloor;
    SchemaVersion = CurrentSchemaVersion;
  }

  // One entry per pack, each beside its own weight. Up to 0.1.1 the default
  // pack was added in front of the saved list on every launch, so a pack
  // listed more than once keeps its last entry and that entry's weight: the
  // earlier copies are the added ones.
  private void NormalizePacks()
  {
    SpritePacks ??= new List<string>();
    PackWeights ??= new List<float>();

    var seen = new HashSet<string>(StringComparer.Ordinal);
    var packs = new List<string>(SpritePacks.Count);
    var weights = new List<float>(SpritePacks.Count);
    for (int index = SpritePacks.Count - 1; index >= 0; index--)
    {
      string pack = SpritePacks[index];
      if (string.IsNullOrWhiteSpace(pack) || !seen.Add(pack))
        continue;
      packs.Add(pack);
      weights.Add(index < PackWeights.Count ? PackWeights[index] : 1f);
    }
    packs.Reverse();
    weights.Reverse();

    if (packs.Count == 0)
    {
      packs.Add(PlaceholderArt.DefaultPack);
      weights.Add(1f);
    }

    // in place: the settings UI holds these lists
    SpritePacks.Clear();
    SpritePacks.AddRange(packs);
    PackWeights.Clear();
    PackWeights.AddRange(weights);
    SyncPackWeights();
  }

  public void SyncPackWeights()
  {
    if (PackWeights == null)
      PackWeights = new List<float>();

    while (PackWeights.Count > SpritePacks.Count)
      PackWeights.RemoveAt(PackWeights.Count - 1);

    while (PackWeights.Count < SpritePacks.Count)
      PackWeights.Add(1f);

    for (int index = 0; index < PackWeights.Count; index++)
    {
      if (!(PackWeights[index] > 0f))
        PackWeights[index] = 1f;
    }
  }

  public override void Save(UnityModManager.ModEntry modEntry)
  {
    Normalize();
    Save(this, modEntry);
  }
}
