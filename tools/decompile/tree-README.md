# Warpforge game code (decompiled)

The whole of the game's own code - `Assembly-CSharp` (the game) and `Assembly-CSharp-firstpass` (the libraries
compiled with it: Photon PUN, UI Extensions, anti-cheat, console) - decompiled from the compiled game
(`libil2cpp.so`, Android build of game 1.35.0; the PC build is the same code, see `pc-android-code-match.md`) and
rewritten to read like C#. Made on 2026-11-02 for writing hooks and learning how the game works. **It is
pseudo-code: it does not compile and is not meant to.** Game content: keep it out of public repositories.

Not included: Unity's own assemblies, .NET (mscorlib, System.*), PlayFab, Newtonsoft, DOTween - their source is
public. The method table `methods.tsv` covers every game method (address -> type, method, file).

## Layout

One file per top-level type, named after the type, in folders by area (`INDEX.md` lists every file):

| Folder | What |
|---|---|
| `Battle/` | everything inside a match: `Core` (BattleManager, turns, players), `Cards` (CardScript, EntityScript), `Abilities` (abilities, effects, criteria, enchantments, traits), `AI`, `Network` (match networking, matchmaking, MatchData), `UI`, `Replay`, `Tutorial` |
| `Cards/` | card data model (`Data`: RawCardScript, factions, keywords) and card rendering outside a match (`Visuals`) |
| `Decks/` | deck building and the deck editor |
| `Menu/` | the main menu by screen: `Main`, `Collection`, `Shop`, `Rewards`, `Social`, `Events`, `Campaign`, `Profile`, `Windows` (window/tab system, popups), `Onboarding` |
| `Player/` | player data and progression (PlayerDataManager, currencies, inventory) |
| `LiveOps/` | the `Everguild.LiveOps` server-driven config and event system |
| `Services/` | PlayFab, Azure, login, analytics, notifications, support, purchasing |
| `Localization/`, `Audio/`, `VFX/`, `UI/Common/`, `Utils/`, `Addressables/`, `Input/` | as named |
| `ThirdParty/` | `Assembly-CSharp-firstpass` by namespace (Photon, UnityEngine.UI.Extensions, CodeStage anti-cheat, ...) |
| `Generated/` | compiler-generated anonymous types |

Nested types (including the compiler's coroutine state machines and lambda closures) are inside their outer
type's file, each with a comment saying what it is.

## How to read a file

```
class BattleCommsManager : MonoBehaviour
{
    // fields (offset in the object)
    /* 0x20 */   PhotonView photonView;          <- real memory offset: *(this + 0x20)
    ...
    public void SendForfeit()   // 0x32B3CF8     <- address of the method's code in libil2cpp.so
    {
        // locals: PhotonView photonView;        <- locals the decompiler could type
        ...
```

- **Names are real** where the metadata has them: types, methods, fields, parameters, enum values and string
  literals. `this.field` / `obj.field` were rebuilt from memory offsets wherever the object's type was known;
  a leftover `*(x + 0x38)` means the type of `x` could not be worked out (look the offset up in the field table of
  the type you think `x` is).
- Local variable names are **guesses from what they hold** (`matchData = this.matchData`); Ghidra's own names
  (`uVar3`, `lVar8`, `uStack_48`) are left where nothing better was known. A variable the decompiler reused for
  values of different types is split into `x`, `x_2`, ...
- `obj.Method(args)`, `obj.Property`, `list[i]`, `list._size` (= Count), `arr.Length`, `new Foo(...)`,
  `(object)x` (boxing), `x == null` all mean what they say in C#. `X.Y   // interface call IFoo` is a call
  through an interface; `obj.virtual_slot_N(...)` a virtual call whose target could not be named.
- `ObscuredInt` / `ObscuredBool` / ... are the anti-cheat wrappers around numbers (health, attack, currencies):
  reads go through `ObscuredInt.op_Implicit` / `.ToInt()`; the fields `hiddenValue`, `fakeValue` and `inited`
  belong to the wrapper.
- IL2CPP's own bookkeeping was removed: metadata/class initialisation, write barriers, GC card marks.
  Null checks the compiler inserted remain as `if (x == null) throw new NullReferenceException();` /
  `goto LAB_...` jumps to a throw - the original C# just dereferenced `x`.
  `throw new InvalidOperationException();   // index/cast check failed` is the compiler's bounds or cast check.
- Coroutines: the C# `IEnumerator Foo()` body lives in the nested `<Foo>d__N` type's `MoveNext()`, as a
  `switch` on `this.state`; `this.outer` is the enclosing object, `this.current` the yielded value, and the
  method's locals are fields of the state machine. Lambdas live in `<>c` (static) or `<>c__DisplayClassN_M`
  (capturing) nested types, named `<Method>b__N_M`.
- `LAB_...:` labels and `goto`s are the decompiler's; long methods with many early-outs read better bottom-up.
- Long methods (the battle coroutines) can come out tangled or partly; the calls and field accesses are right,
  the control flow is to be read with care.

## Hooking

Harmony hooks go on the method as declared (type and method name from the file header, parameters as listed).
The address comment (`// 0x32B3CF8`) is the Android address; `methods.tsv` maps every method's address to its
file for looking something up from a stack trace. Field offsets are the same on PC and Android (both 64-bit
IL2CPP from the same source).

## Coverage

See the end of `INDEX.md`. Methods that could not be decompiled are marked `// 0x...: not decompiled` in place.
