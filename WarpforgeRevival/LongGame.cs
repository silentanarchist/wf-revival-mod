using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using Il2Cpp;
using Il2CppEverguild.LiveOps;

namespace WarpforgeRevival
{
    /// <summary>
    /// Constructed formats with their own decks next to Classic and Skirmish: the "Long Game" (Custom Test),
    /// and since 0.14.52 any mode the server gives a deck list of its own ("deckListMode" in its gameplay
    /// variables, sent to the mod as "deckModes"; first: the custom-card test mode).
    /// The game files a deck under a play mode and only knows Classic and Skirmish decks, so each such
    /// format's decks are stored under a play mode the game never uses (Long Game: Battle4Warpforge; the
    /// server names the others from Dungeon, RankedFriendly, OwnDeckTraining). That keeps them separate
    /// sets for good: nothing has to be converted later. Where the game would otherwise not show them,
    /// they are presented as Classic decks:
    ///  - on the format's page (and the deck editor opened from it) its decks are listed and Classic
    ///    decks are not, and a deck created there becomes one of its decks;
    ///  - on any other mode page they are not listed;
    ///  - in the Collection they appear next to the Classic decks so they can be edited or deleted.
    /// Such decks always follow their event's rules (deck size, copies). The Long Game also has copies
    /// per rarity and the warlord health multiplier, from the server's revival-settings.json ("longGame").
    /// </summary>
    internal static class LongGame
    {
        private static string pageFormat;                // event id of the format whose page is open (null: none, or a normal mode)
        private static string editingFormat;             // event id of the format whose deck is open in the editor (null: none)
        private static readonly Dictionary<string, IPlayEvent> events = new Dictionary<string, IPlayEvent>();
        private const PlayModes LongMode = PlayModes.Battle4Warpforge;
        private static bool inside;                      // guards against re-entry from our own calls

        private static string EventId => ServerSettings.LongGameEvent;
        private static bool editingLong => editingFormat != null && editingFormat == EventId;
        private static bool onLongPage => pageFormat != null && pageFormat == EventId;

        /// <summary>The stored play mode of a format's decks, or null when the event is not a format with its own decks.</summary>
        private static PlayModes? ModeOf(string eventId)
        {
            if (string.IsNullOrEmpty(eventId)) return null;
            if (eventId == EventId) return LongMode;
            return ServerSettings.DeckModes.TryGetValue(eventId, out int m) && m > 0 ? (PlayModes)m : (PlayModes?)null;
        }

        /// <summary>The format whose decks are stored under this play mode, or null.</summary>
        private static string FormatOfMode(PlayModes mode)
        {
            if (mode == LongMode && !string.IsNullOrEmpty(EventId)) return EventId;
            foreach (var kv in ServerSettings.DeckModes) if (kv.Value == (int)mode) return kv.Key;
            return null;
        }

        /// <summary>The event id when the event is a format with its own decks, else null.</summary>
        private static string FormatOf(IPlayEvent e)
        {
            try
            {
                if ((object)e == null) return null;
                var data = e.GetBaseData();
                string id = (object)data == null ? null : data.eventId;
                if (ModeOf(id) == null) return null;
                events[id] = e;
                return id;
            }
            catch { return null; }
        }

        /// <summary>A format's deck is or becomes one of its decks: a deck stored as any other format's stays as it is.</summary>
        private static void MakeFormat(CardDeck deck, string format)
        {
            var mode = ModeOf(format);
            if (mode == null || (object)deck == null || deck.Pointer == IntPtr.Zero) return;
            if (TryStoredMode(deck, out var stored)) { if (stored == mode.Value || stored != PlayModes.Classic) return; }   // never a Skirmish deck
            StoreMode(deck, mode.Value);
            RevivalMod.Log.Msg($"[long] '{deck.deckName}' is now a {format} deck");
        }

        /// <summary>The format a deck is stored under, or null (Classic, Skirmish, draft).</summary>
        private static string StoredFormat(CardDeck deck)
        {
            if ((object)deck == null || deck.Pointer == IntPtr.Zero || !TryStoredMode(deck, out var stored)) return null;
            return FormatOfMode(stored);
        }

        /// <summary>Whether a deck is stored as one of the given format's decks.</summary>
        internal static bool IsStoredAs(CardDeck deck, string format) => format != null && StoredFormat(deck) == format;

        /// <summary>The format a deck belongs to right now: stored as one, or the editor's working copy of one opened from a format's list.</summary>
        internal static string FormatOfDeck(CardDeck deck)
        {
            if ((object)deck == null || deck.Pointer == IntPtr.Zero) return null;
            if (TryStoredMode(deck, out var stored))
            {
                var f = FormatOfMode(stored);
                if (f != null) return f;
                return stored == PlayModes.Classic ? editingFormat : null;
            }
            return editingFormat;
        }

        private static IPlayEvent Event() => EventOf(EventId);

        /// <summary>A format's event (looked up once by id).</summary>
        private static IPlayEvent EventOf(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (events.TryGetValue(id, out var known) && (object)known != null) return known;
            try
            {
                var found = LiveOpsManager.GetEvent(id);
                var e = (object)found == null ? null : found.TryCast<IPlayEvent>();
                if ((object)e != null) events[id] = e;
                return e;
            }
            catch { return null; }
        }

        internal static bool IsLongEvent(IPlayEvent e) => !string.IsNullOrEmpty(EventId) && FormatOf(e) == EventId;

        /// <summary>True when the event is a format with decks of its own (the Long Game or a server-named mode).</summary>
        internal static bool HasOwnDecks(IPlayEvent e) => FormatOf(e) != null;

        // A deck's play mode is an optional value stored inside the deck (CardDeck.gameMode, a PlayModes?: the
        // "has a value" flag, then the mode 4 bytes on). The generated wrapper for that field reads it wrongly, so
        // it is read and written directly. Its place is asked of the game once (M-21, 0.13.72: it was a fixed
        // 0x70, never checked on the phone build), with 0x70 kept only if the game cannot say.
        private static int modeField = -1;

        private static int ModeField()
        {
            if (modeField >= 0) return modeField;
            int at = 0x70;
            try
            {
                var field = Il2CppInterop.Runtime.IL2CPP.GetIl2CppField(Il2CppInterop.Runtime.Il2CppClassPointerStore<CardDeck>.NativeClassPtr, "gameMode");
                if (field != IntPtr.Zero)
                {
                    int found = (int)Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(field);
                    if (found > 0 && found < 0x1000) at = found;
                }
                RevivalMod.Log.Msg($"[long] a deck's play mode is kept at 0x{at:X}" + (at != 0x70 ? " (not 0x70 as on PC)" : ""));
            }
            catch (Exception e) { RevivalMod.Log.Warning("[long] deck play mode field: " + e.Message + "; using 0x70"); }
            return modeField = at;
        }

        private static bool TryStoredMode(CardDeck deck, out PlayModes mode)
        {
            int at = ModeField();
            mode = (PlayModes)System.Runtime.InteropServices.Marshal.ReadInt32(deck.Pointer + at + 4);
            return System.Runtime.InteropServices.Marshal.ReadByte(deck.Pointer + at) != 0;
        }

        private static void StoreMode(CardDeck deck, PlayModes mode)
        {
            int at = ModeField();
            System.Runtime.InteropServices.Marshal.WriteInt32(deck.Pointer + at + 4, (int)mode);
            System.Runtime.InteropServices.Marshal.WriteByte(deck.Pointer + at, 1);
        }

        /// <summary>A deck stored as a Long Game (Custom Test) deck, whatever page is open.</summary>
        internal static bool IsStoredLong(CardDeck deck)
        {
            try { return !string.IsNullOrEmpty(EventId) && StoredAsLong(deck); } catch { return false; }
        }

        /// <summary>A deck stored as one of any own-deck format's decks (never usable in Classic or Skirmish), and which.</summary>
        internal static string StoredOwnFormat(CardDeck deck)
        {
            try { return StoredFormat(deck); } catch { return null; }
        }

        /// <summary>Forgets that a format's page is open (a friend challenge is never one of them; see ChallengeDecks).</summary>
        internal static void LeavePage()
        {
            if (pageFormat != null || editingFormat != null) RevivalMod.Log.Msg("[long] format page state cleared for a friend challenge");
            pageFormat = null; editingFormat = null;
        }

        /// <summary>Whether this is a Long Game deck (stored under the Long Game play mode, or being edited from its list).</summary>
        internal static bool Applies(CardDeck deck) => !string.IsNullOrEmpty(EventId) && FormatOfDeck(deck) == EventId;

        private static bool StoredAsLong(CardDeck deck) => IsStoredAs(deck, EventId);

        /// <summary>How a deck's play mode is presented to the rest of the game (see the class summary).</summary>
        [HarmonyPatch(typeof(CardDeck), nameof(CardDeck.GameMode), MethodType.Getter)]
        private static class ShownMode
        {
            private static void Postfix(CardDeck __instance, ref PlayModes __result)
            {
                try
                {
                    if (saving > 0) return;      // what gets saved is the real mode
                    string open = pageFormat ?? editingFormat;
                    var ownFormat = FormatOfMode(__result);
                    if (ownFormat != null) { if (open != null && open == ownFormat) __result = PlayModes.Classic; }
                    else if (__result == PlayModes.Classic && open != null) __result = ModeOf(open) ?? __result;
                }
                catch { }
            }
        }

        /// <summary>A deck opened in the editor from the Long Game page is, or becomes, a Long Game deck.</summary>
        [HarmonyPatch(typeof(DeckEditingPanel), nameof(DeckEditingPanel.Initialize))]
        private static class EditorOpened
        {
            private static void Prefix(CardDeck deck)
            {
                try
                {
                    if (editingFormat != null) MakeFormat(deck, editingFormat);
                }
                catch (Exception e) { RevivalMod.Log.Warning("[long] could not mark the deck's format: " + e.Message); }
            }

            // The editor works on its own copy of the deck; make sure that copy, and what gets saved, is a
            // Long Game deck too. (One patch class with the prefix since 0.13.75, M-26.)
            private static void Postfix(DeckEditingPanel __instance)
            {
                try { if (editingFormat != null) MakeFormat(__instance.editingDeck, editingFormat); }
                catch (Exception ex) { RevivalMod.Log.Warning("[long] editor copy: " + ex.Message); }
            }
        }

        // ------------------------------------------------------------------ which page is open

        private static void SetPage(IPlayEvent e, string seenFrom)
        {
            string format = FormatOf(e);
            string id = "?";
            try { var d = (object)e == null ? null : e.GetBaseData(); if ((object)d != null) id = d.eventId; } catch { }
            pageFormat = format;
            editingFormat = null;
            RevivalMod.Log.Msg($"[long] {seenFrom}: page for event '{id}' -> {(format != null ? "its own decks only" : "own-deck formats hidden")}");
        }

        // The deck picker on a mode page rebuilds its list here (when the page opens and whenever the
        // faction changes); the page's event tells us which mode's decks to present.
        [HarmonyPatch(typeof(RankedDeckSelector), nameof(RankedDeckSelector.ChangeArmy))]
        private static class PageOpened
        {
            private static void Prefix(RankedDeckSelector __instance)
            {
                try { SetPage(__instance.currentEvent, "deck picker"); }
                catch (Exception ex) { RevivalMod.Log.Warning("[long] deck picker: " + ex); }
            }
        }

        // A Play-screen tile opens "the ranked window", and the game then looks up the one Classic-type
        // event it expects to exist, so the Long Game tile would open the Classic page. Remember which
        // tile was clicked and hand the window the Long Game event instead. The same for every other
        // tester-mode tile (0.14.34: the server-deal test, a Classic-rules mode with Classic decks).
        private static float longTileClickedAt = -100f;
        private static IPlayEvent clickedEvent;
        // The draft run being looked at (0.14.41): the game asks GameModes.GetActiveEvent(ClosedDeck) for "the draft
        // event" in places that should mean this one - CardDeck.EmptySlotsCount (the deck's size and the "x/N" count,
        // the deck validity before a draft match) - and gets the first draft event (Classic Draft) otherwise.
        private static IPlayEvent lastDraft;

        [HarmonyPatch(typeof(LiveopMenuContainer), nameof(LiveopMenuContainer.OnClick))]
        private static class TileClicked
        {
            private static void Prefix(LiveopMenuContainer __instance)
            {
                try
                {
                    var content = __instance.Content;
                    var e = (object)content == null ? null : content.TryCast<IPlayEvent>();
                    bool remember = FormatOf(e) != null || TestersPage.IsTesterEvent(e);
                    clickedEvent = remember ? e : null;
                    try { if ((object)e != null && (object)e.TryCast<IDraftEvent>() != null) lastDraft = e; } catch { }
                    longTileClickedAt = remember ? UnityEngine.Time.realtimeSinceStartup : -100f;
                    string id = "?";
                    try { var d = (object)e == null ? null : e.GetBaseData(); if ((object)d != null) id = d.eventId; } catch { }
                    RevivalMod.Log.Msg($"[long] tile clicked: '{id}'");
                }
                catch (Exception ex) { RevivalMod.Log.Warning("[long] tile: " + ex); }
            }
        }

        [HarmonyPatch(typeof(RankedEventWindowV2), nameof(RankedEventWindowV2.TryOpen))]
        private static class WindowV2
        {
            private static void Prefix(ref Il2CppSystem.Object data)
            {
                try
                {
                    bool fromLongTile = UnityEngine.Time.realtimeSinceStartup - longTileClickedAt < 3f;
                    longTileClickedAt = -100f;
                    var wanted = clickedEvent ?? Event();
                    if (fromLongTile && (object)wanted != null)
                    {
                        data = new Il2CppSystem.Object(wanted.Pointer);
                        string id = "?";
                        try { var d = wanted.GetBaseData(); if ((object)d != null) id = d.eventId; } catch { }
                        RevivalMod.Log.Msg("[long] opening the mode window for " + id);
                    }
                    clickedEvent = null;
                    var e = (object)data == null ? null : data.TryCast<IPlayEvent>();
                    if ((object)e != null) SetPage(e, "mode window");
                }
                catch (Exception ex) { RevivalMod.Log.Warning("[long] mode window: " + ex); }
            }
        }

        // The deck list screen: the player first picks a mode, then sees that mode's decks.
        // hooked through SharedHooks (one hook per game method): HarmonyPatch(typeof(SelectDecksTab), nameof(SelectDecksTab.ToggleSelectDeck))
        internal static class DeckListMode
        {
            internal static void Prefix(IOwnDeckPlayEvent deckPlayEvent)
            {
                try { filterLines = 12; SetPage((object)deckPlayEvent == null ? null : deckPlayEvent.TryCast<IPlayEvent>(), "deck list"); }
                catch (Exception ex) { RevivalMod.Log.Warning("[long] deck list: " + ex); }
            }
        }

        [HarmonyPatch(typeof(SelectDecksTab), nameof(SelectDecksTab.ToggleSelectGameModeDeck))]
        private static class DeckListModeChooser
        {
            private static void Prefix() { pageFormat = null; editingFormat = null; }
        }

        // Whatever opens the deck editor while a format's list is showing is editing one of its decks.
        [HarmonyPatch(typeof(DeckEditingWindow), nameof(DeckEditingWindow.TryOpen))]
        private static class EditorOpening
        {
            private static void Prefix(Il2CppSystem.Object data)
            {
                editorOpen = true; browseActive = false;
                try
                {
                    if (pageFormat == null) return;
                    editingFormat = pageFormat;
                    var deck = (object)data == null ? null : data.TryCast<CardDeck>();
                    RevivalMod.Log.Msg($"[long] deck editor opening from the {pageFormat} list (given: {((object)data == null ? "nothing" : data.GetIl2CppType().Name)})");
                    MakeFormat(deck, editingFormat);
                }
                catch (Exception ex) { RevivalMod.Log.Warning("[long] editor: " + ex); }
            }
        }

        [HarmonyPatch(typeof(DeckEditingWindow), nameof(DeckEditingWindow.TrySaveDeck))]
        private static class Saving
        {
            private static void Prefix(DeckEditingWindow __instance)
            {
                try
                {
                    if (editingFormat == null) return;
                    var panel = __instance.editingPanel;
                    if ((object)panel != null) MakeFormat(panel.editingDeck, editingFormat);
                }
                catch (Exception ex) { RevivalMod.Log.Warning("[long] saving: " + ex.Message); }
            }
        }

        // While a deck is being written out (to the server or to text), nothing is disguised: the saved
        // deck must carry its real play mode or it would come back as a Classic deck.
        private static int saving;

        [HarmonyPatch(typeof(PlayerDataManager), nameof(PlayerDataManager.UploadUnsyncedDeck))]
        private static class Uploading
        {
            private static void Prefix(CardDeck deckData)
            {
                try { var f = editingFormat ?? pageFormat; if (f != null) MakeFormat(deckData, f); } catch { }
                saving++;
            }
            private static void Finalizer() { if (saving > 0) saving--; }
        }

        [HarmonyPatch(typeof(PlayerDataManager), nameof(PlayerDataManager.ExtractDeckInfo))]
        private static class Extracting
        {
            private static void Prefix() { saving++; }
            private static void Finalizer() { if (saving > 0) saving--; }
        }

        [HarmonyPatch(typeof(CardDeck), nameof(CardDeck.Serialize))]
        private static class Serializing
        {
            private static void Prefix() { saving++; }
            private static void Finalizer() { if (saving > 0) saving--; }
        }

        // A copy of a Long Game deck is a Long Game deck.
        [HarmonyPatch(typeof(CardDeck), nameof(CardDeck.CopyDeck))]
        private static class Copying
        {
            private static void Postfix(CardDeck __instance, CardDeck deckToCopy)
            {
                try { var f = StoredFormat(deckToCopy); if (f != null) MakeFormat(__instance, f); } catch { }
            }
        }

        // The game finds a deck's rules by asking for "the active event of the deck's play mode".
        // Long Game's own play mode has no event as far as the game knows, so answer for it; and while a
        // Long Game page or editor is open, the mode the game thinks it is in is really Long Game.
        [HarmonyPatch(typeof(GameModes), nameof(GameModes.GetActiveEvent), new[] { typeof(PlayModes) })]
        private static class ActiveEvent
        {
            private static void Postfix(PlayModes playMode, ref IPlayEvent __result)
            {
                try
                {
                    if (playMode == PlayModes.ClosedDeck && (object)lastDraft != null)
                    {
                        // the draft run last opened from its tile, not whichever draft event comes first
                        __result = lastDraft;
                        return;
                    }
                    string f = FormatOfMode(playMode) ?? (playMode == PlayModes.Classic ? pageFormat ?? editingFormat : null);
                    if (f != null)
                    {
                        var e = EventOf(f);
                        if ((object)e != null) __result = e;
                    }
                }
                catch { }
            }
        }

        // The deck list decides what to show by comparing play modes, and Long Game and Classic share
        // one, so say explicitly which decks belong in which list.
        private static int filterLines;

        [HarmonyPatch(typeof(DeckCollectionTab), nameof(DeckCollectionTab._GetCollection_b__7_0))]
        private static class DeckListFilter
        {
            private static void Postfix(DeckCollectionTab __instance, CardDeck deck, ref bool __result)
            {
                try
                {
                    if ((object)deck == null) return;
                    var current = __instance.CurrentEvent;
                    string listFormat = FormatOf((object)current == null ? null : current.TryCast<IPlayEvent>());
                    string deckFormat = StoredFormat(deck);
                    bool game = __result;
                    if (deckFormat != null) __result = listFormat == deckFormat;
                    else if (listFormat != null) __result = false;
                    if (filterLines > 0)
                    {
                        filterLines--;
                        bool has = TryStoredMode(deck, out var stored);
                        RevivalMod.Log.Msg($"[long] list check '{deck.deckName}': stored mode {(has ? stored.ToString() : "none")}, {deck.CardCount(false)} cards, list is {listFormat ?? "other"}, game said {game} -> {__result}");
                    }
                }
                catch (Exception ex) { RevivalMod.Log.Warning("[long] deck list filter: " + ex.Message); }
            }
        }

        [HarmonyPatch(typeof(RankedDeckSelector), nameof(RankedDeckSelector.OnDisable))]
        private static class PageClosed
        {
            private static void Postfix() { pageFormat = null; }
        }

        [HarmonyPatch(typeof(RankedDeckSelector), nameof(RankedDeckSelector.OnCreateDeckButtonClick))]
        private static class CreateFromPage
        {
            private static void Prefix() { if (pageFormat != null) editingFormat = pageFormat; }
        }

        [HarmonyPatch(typeof(RankedDeckSelector), nameof(RankedDeckSelector.OnViewDeckButtonClick))]
        private static class ViewFromPage
        {
            private static void Prefix() { if (pageFormat != null) editingFormat = pageFormat; }
        }

        [HarmonyPatch(typeof(DeckEditingWindow), nameof(DeckEditingWindow.Close))]
        private static class EditorClosed
        {
            private static void Postfix() { editingFormat = null; editorOpen = false; }
        }

        // ------------------------------------------------------------------ deck rules

        [HarmonyPatch(typeof(CardDeck), nameof(CardDeck.CustomGameModeEvent), MethodType.Getter)]
        private static class RulesForDeck
        {
            private static void Postfix(CardDeck __instance, ref IPlayEvent __result)
            {
                if (inside) return;
                inside = true;
                try
                {
                    string f = FormatOfDeck(__instance);
                    if (f == null) return;
                    var e = EventOf(f);
                    if ((object)e != null) __result = e;
                }
                catch (Exception ex) { RevivalMod.Log.Warning("[long] deck rules: " + ex.Message); }
                finally { inside = false; }
            }
        }

        private static bool copiesNoted;

        [HarmonyPatch(typeof(CardDeck), nameof(CardDeck.CanAddCard))]
        private static class CopiesByRarity
        {
            private static void Postfix(CardDeck __instance, RawCardScript card, ref string error, ref bool __result)
            {
                try
                {
                    if (!__result || (object)card == null || card.cardType != CardTypeOptions.Minion && card.cardType != CardTypeOptions.Tactic) return;
                    if (!Applies(__instance)) return;
                    int limit = ServerSettings.LongGameCopies((int)card.cardRarity);
                    if (limit <= 0) return;
                    int have = __instance.CardCopiesInDeck(card);
                    if (have < limit) return;
                    __result = false;
                    error = Loc.T("decks.customTestCopies", "Custom Test allows {limit} of a {rarity} card.", ("limit", limit),
                                  ("rarity", Loc.Game("Card_Rarity/" + card.cardRarity, card.cardRarity.ToString())));     // the game's own rarity name
                    if (!copiesNoted) { copiesNoted = true; RevivalMod.Log.Msg($"[long] copy limit applied: '{card.cardName}' ({card.cardRarity}) is at {have} of {limit}"); }
                }
                catch (Exception e) { RevivalMod.Log.Warning("[long] copies: " + e.Message); }
            }
        }

        /// <summary>
        /// The deck editor writes "in deck / allowed" under each card using the game's two-tier limit
        /// (Legendary, everything else). Show the Long Game limit for the card's rarity instead.
        /// </summary>
        [HarmonyPatch(typeof(CollectionCard), nameof(CollectionCard.UpdateCardVisuals))]
        private static class CopiesShown
        {
            private static void Prefix(CollectionCard __instance, ref string counterText)
            {
                try
                {
                    if (!editingLong || string.IsNullOrEmpty(counterText)) return;
                    int slash = counterText.IndexOf('/');
                    if (slash <= 0 || !int.TryParse(counterText.Substring(slash + 1), out int shown)) return;
                    var card = __instance.Item;
                    if ((object)card == null) return;
                    int limit = ServerSettings.LongGameCopies((int)card.cardRarity);
                    if (limit > 0 && limit < shown) counterText = counterText.Substring(0, slash + 1) + limit;
                }
                catch { }
            }
        }

        // ------------------------------------------------------------------ starting hand

        // The starting hand sizes live in the battle's own settings, which are shared by every match,
        // so set them for a Long Game match and put the game's values back for any other.
        private static int gameHand = -1, gameSecondExtra = -1;

        /// <summary>The game's own starting hand and second player's extra cards (seen at the first match; -1 before).</summary>
        internal static (int hand, int extra) GameDefaultHand => (gameHand, gameSecondExtra);

        /// <summary>The game mode being played (null when not known).</summary>
        private static IPlayEvent CurrentPlayEvent()
        {
            try { var modes = LiveOpsManager.GetHandler<GameModes>(); return (object)modes == null ? null : modes.CurrentPlayingEvent; }
            catch { return null; }
        }

        internal static bool InLongMatch() => IsLongEvent(CurrentPlayEvent());

        /// <summary>Event id of the game mode being played (null when not known).</summary>
        internal static string PlayingEventId()
        {
            try
            {
                var e = CurrentPlayEvent();
                var data = (object)e == null ? null : e.GetBaseData();
                return (object)data == null ? null : data.eventId;
            }
            catch { return null; }
        }

        private static void ApplyHand(PlayerHand hand) => ApplyHand(hand.manager);

        private static void ApplyHand(BattleManager battle)
        {
            if ((object)battle == null) return;
            var vars = battle.scenarioVariables;
            if ((object)vars == null) return;
            if (gameHand < 0) { gameHand = vars.startingHand; gameSecondExtra = vars.secondExtraCards; }
            bool isLong = InLongMatch();
            int first = isLong && ServerSettings.LongGameHand > 0 ? ServerSettings.LongGameHand : gameHand;
            int extra = isLong && ServerSettings.LongGameSecondExtra >= 0 ? ServerSettings.LongGameSecondExtra : gameSecondExtra;
            // a game mode's own setting (its gameplayVariables on the server) comes first
            string mode = PlayingEventId();
            var (modeHand, modeExtra) = ServerSettings.ModeHand(mode);
            if (modeHand > 0) first = modeHand;
            if (modeExtra >= 0) extra = modeExtra;
            // a friend challenge with custom rules comes before everything
            if (ChallengeRules.StartingHand is int ch && ch > 0) first = ch;
            if (ChallengeRules.SecondExtra is int ce && ce >= 0) extra = ce;
            if (vars.startingHand != first || vars.secondExtraCards != extra)
            {
                vars.startingHand = first;
                vars.secondExtraCards = extra;
                RevivalMod.Log.Msg($"[hand] starting hand: {first} cards, +{extra} for the second player ({(modeHand > 0 || modeExtra >= 0 ? mode : isLong ? "Long Game" : "game default")})");
            }
        }

        // PlayerHand.GetBaseCardsInHandCount / GetSecondExtraCardsCount are folded into the dealing code by
        // the compiler (no call to either is left in the game, checked 2026-10-17), so the values must be in
        // place before the opening hands are dealt. (The hooks on those two getters were removed in 0.13.72.)
        [HarmonyPatch(typeof(PlayerHand), nameof(PlayerHand.AddCardsToMulligan))]
        private static class HandDealt
        {
            private static void Prefix(PlayerHand __instance)
            {
                try { ApplyHand(__instance); } catch (Exception e) { RevivalMod.Log.Warning("[long] starting hand: " + e.Message); }
            }
        }

        /// <summary>Redraws the number on the warlord (it was drawn before the health was raised).</summary>
        private static void ShowHealth(CardScript hero)
        {
            try { if ((object)hero != null) hero.UpdateHealthText(false); }
            catch (Exception e) { RevivalMod.Log.Warning("[long] warlord health display: " + e.Message); }
        }

        [HarmonyPatch(typeof(BattleManager), nameof(BattleManager.SetupMulliganPhase))]
        private static class HandPhase
        {
            private static void Prefix(BattleManager __instance)
            {
                try { if (InLongMatch()) { ShowHealth(__instance.GetHero(true)); ShowHealth(__instance.GetHero(false)); } } catch { }
                try { ApplyHand(__instance); } catch (Exception e) { RevivalMod.Log.Warning("[long] starting hand: " + e.Message); }
            }
        }

        // ------------------------------------------------------------------ warlord health in the deck builder
        // The deck builder shows a warlord with the health it has under the rules of the mode being
        // built for (the game does that itself for Classic and Skirmish). Long Game's health is a
        // multiplier applied by this mod, so the builder is told the Long Game figure here.
        private static int builderNotes, browseNotes;
        private static bool editorOpen;                  // a deck is being built or edited (any mode)

        private static bool browseActive;                // the plain collection (no deck open) was the last card grid drawn

        // (Hooking the function every card display goes through, BasicCardUI.SetRawCardData, broke
        // card drawing in 0.11.2, so each screen is adjusted at its own, narrower, entry point.)

        /// <summary>The health a warlord card should show right now, or -1 to leave what the game wrote.</summary>
        private static int ShownHealth(RawCardScript item, int change, bool grid)
        {
            if ((object)item == null || item.cardType != CardTypeOptions.Hero) return -1;
            if (onLongPage || editingLong)
            {
                double factor = ServerSettings.LongGameHealth;
                if (factor <= 0 || Math.Abs(factor - 1.0) < 0.001) return -1;
                int before = item.maxHealth + change;
                // the multiplier applies to the card's own health first, then the mode's change is added
                int after = (int)Math.Round(item.maxHealth * factor, MidpointRounding.AwayFromZero) + change;
                if (builderNotes < 3) { builderNotes++; RevivalMod.Log.Msg($"[long] deck builder shows Long Game warlord health ('{item.cardName}' {before} -> {after}, {(grid ? "card grid" : "enlarged card")})"); }
                return after;
            }
            // Browsing the collection (no deck being built): show warlords as Classic has them.
            int classic = ServerSettings.PracticeClassicLife;
            if (editorOpen || change != 0 || classic == 0) return -1;
            if (grid) browseActive = true;
            else if (!browseActive) return -1;
            if (browseNotes < 3) { browseNotes++; RevivalMod.Log.Msg($"[cards] collection shows Classic warlord health ('{item.cardName}' {item.maxHealth} -> {item.maxHealth + classic}, {(grid ? "card grid" : "enlarged card")})"); }
            return item.maxHealth + classic;
        }

        private static void ApplyGrid(CollectionCard card)
        {
            try
            {
                if ((object)card == null) return;
                var ui = card.cardUI;
                if ((object)ui == null) return;
                var data = card.currentGameplayVariablesData;
                int health = ShownHealth(card.Item, (object)data != null ? data.warlordLifeChange : 0, true);
                if (health >= 0) ui.SetAltHealth(health);
            }
            catch (Exception e) { if (builderNotes < 6) { builderNotes++; RevivalMod.Log.Warning("[long] deck builder health: " + e.Message); } }
        }

        [HarmonyPatch(typeof(CollectionCard), nameof(CollectionCard.Config))]
        private static class BuilderHealth
        {
            private static void Postfix(CollectionCard __instance) { ApplyGrid(__instance); }
        }

        // The grid writes each card's numbers again after setting it up, and again whenever the deck changes.
        [HarmonyPatch(typeof(CardCollectionDisplay), nameof(CardCollectionDisplay.SetCell))]
        private static class GridCell
        {
            private static void Postfix(Il2CppPolyAndCode.UI.ICell cell)
            {
                try { ApplyGrid((object)cell == null ? null : cell.TryCast<CollectionCard>()); }
                catch (Exception e) { if (builderNotes < 6) { builderNotes++; RevivalMod.Log.Warning("[long] deck builder health (cell): " + e.Message); } }
            }
        }

        [HarmonyPatch(typeof(CardCollectionDisplay), nameof(CardCollectionDisplay.UpdateCardVisuals))]
        private static class GridRefresh
        {
            private static void Postfix(CollectionCard card) { ApplyGrid(card); }
        }

        // The deck editor has its own grid, which does the same rewriting.
        [HarmonyPatch(typeof(DeckEditorCollectionDisplay), nameof(DeckEditorCollectionDisplay.SetCell))]
        private static class EditorCell
        {
            private static void Postfix(Il2CppPolyAndCode.UI.ICell cell)
            {
                try { ApplyGrid((object)cell == null ? null : cell.TryCast<CollectionCard>()); }
                catch (Exception e) { if (builderNotes < 6) { builderNotes++; RevivalMod.Log.Warning("[long] deck builder health (editor cell): " + e.Message); } }
            }
        }

        [HarmonyPatch(typeof(DeckEditorCollectionDisplay), nameof(DeckEditorCollectionDisplay.DrawCell))]
        private static class EditorRedraw
        {
            private static void Postfix(CollectionCard card) { ApplyGrid(card); }
        }

        // The enlarged card shown when one is clicked.
        [HarmonyPatch(typeof(CardDisplayWindow), nameof(CardDisplayWindow.InitializeCardForDisplay))]
        private static class EnlargedCard
        {
            private static void Postfix(CardDisplayWindow __instance, RawCardScript cardData, int index, int warlordHealthModifier)
            {
                try
                {
                    int health = ShownHealth(cardData, warlordHealthModifier, false);
                    if (health < 0) return;
                    var uis = __instance.cardUIs;
                    if (uis == null || index < 0 || index >= uis.Length) return;
                    var ui = uis[index];
                    if ((object)ui != null) ui.SetAltHealth(health);
                }
                catch (Exception e) { if (builderNotes < 6) { builderNotes++; RevivalMod.Log.Warning("[long] enlarged card health: " + e.Message); } }
            }
        }

        // ------------------------------------------------------------------ warlord health

        [HarmonyPatch(typeof(BattleManager), nameof(BattleManager.SetupFullHero))]
        private static class WarlordHealth
        {
            private static void Postfix(BattleManager __instance, bool isPlayer)
            {
                browseActive = false;
                try { ApplyHand(__instance); } catch (Exception e) { RevivalMod.Log.Warning("[long] starting hand: " + e.Message); }
                try
                {
                    // A friend challenge's multiplier and change come first; otherwise Long Game's multiplier.
                    // The multiplier applies to the warlord's own health, then the change is added (the game
                    // has already added the change, so it is taken off, multiplied around, and added back).
                    double factor;
                    int change;
                    if (ChallengeRules.HealthMultiplier is double m) { factor = m; change = ChallengeRules.HealthChange ?? 0; }
                    else if (InLongMatch())
                    {
                        factor = ServerSettings.LongGameHealth;
                        var gv = ChallengeRules.Variables(CurrentPlayEvent());
                        change = (object)gv == null ? 0 : gv.warlordLifeChange;
                    }
                    else return;
                    if (factor <= 0 || Math.Abs(factor - 1.0) < 0.001) return;
                    var hero = __instance.GetHero(isPlayer);
                    if ((object)hero == null) return;
                    int before = hero.currentMaxHealth;
                    int after = (int)Math.Round((before - change) * factor, MidpointRounding.AwayFromZero) + change;
                    hero.currentMaxHealth = after;
                    hero.currentHealth = after;
                    ShowHealth(hero);
                    RevivalMod.Log.Msg($"[long] {(isPlayer ? "your" : "enemy")} warlord health {before} -> {after}");
                }
                catch (Exception e) { RevivalMod.Log.Warning("[long] warlord health: " + e.Message); }
            }
        }
    }
}
