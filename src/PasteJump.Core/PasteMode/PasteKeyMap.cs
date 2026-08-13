using System.Text;

namespace PasteJump.Core.PasteMode;

/// <summary>
/// Which letter fires which paste-mode action, and which actions are switched off.
/// <para>
/// The letters were a hard-coded table in <c>VirtualKeyTranslator</c>. They live here now so the user can move
/// them, and so the rules about what a legal set of bindings looks like are testable without Win32.
/// </para>
/// <para>
/// <b>Only letters are configurable, and that is a safety property rather than a shortcut.</b> The physical keys
/// - the arrows, <c>Home</c>, <c>End</c>, <c>Delete</c>, <c>Enter</c>, <c>Esc</c>, <c>F1</c>, the digits - stay
/// where they are, so no set of bindings can leave a session with no way to step through it and no way out. A
/// letter turned off still has its physical key wherever one exists, and <c>Esc</c> can never be unbound.
/// </para>
/// </summary>
public sealed class PasteKeyMap
{
    /// <summary>
    /// One configurable action: the letter that fires it, whether it is enabled, and the fixed key that also
    /// fires it (or null).
    /// </summary>
    /// <param name="Action">The action itself.</param>
    /// <param name="Name">
    /// Short stable name used in the settings string. Deliberately not <see cref="PasteAction"/>'s own name:
    /// renaming an enum member would then silently orphan everyone's saved binding.
    /// </param>
    /// <param name="Description">What it does, for the settings dialog and the key card.</param>
    /// <param name="DefaultLetter">
    /// Today's letter, which is what a fresh install and a Reset get - or <c>null</c> for an action that ships
    /// switched off, available to anyone who wants it and costing nothing to anyone who does not.
    /// <para>
    /// Only "mark to join" is null, and deliberately. Its letter was in the overlay's key hint, in the F1 card and
    /// in the way of a letter someone might type into search, in exchange for an action most people will never
    /// use: pasting twice is quicker than marking twice and pasting once. Off by default is not the feature being
    /// withdrawn - the Keys tab turns it on like any other letter, and the history window's Copy Joined never
    /// needed a letter at all.
    /// </para>
    /// </param>
    /// <param name="FixedAlias">
    /// A key that fires the action regardless of the letter, shown read-only. This is how nothing that already
    /// works stops working: rebinding pin from <c>P</c> to <c>K</c> leaves <c>Space</c> pinning, and moving
    /// "move to front" off <c>M</c> leaves Clipjump's <c>Q</c> doing it.
    /// </param>
    public sealed record Entry(
        PasteAction Action,
        string Name,
        string Description,
        char? DefaultLetter,
        string? FixedAlias = null);

    /// <summary>
    /// Every configurable action, in the order the settings dialog lists them.
    /// <para>
    /// Actions absent from here are not configurable on purpose: stepping to an older clip is the trigger's own
    /// setting, and the rest are on physical keys whose meaning is not in question.
    /// </para>
    /// </summary>
    public static IReadOnlyList<Entry> Entries { get; } =
    [
        new(PasteAction.Back, "back", "Step back to a newer clip", 'C', "Up / Left"),
        new(PasteAction.JumpToNewest, "newest", "Jump to the newest clip", 'A', "Home"),
        new(PasteAction.ToggleSearch, "search", "Open search", 'F'),
        new(PasteAction.TogglePin, "pin", "Pin or unpin the clip", 'P', "Space"),
        // Off by default - see DefaultLetter. Turning it on is picking any free letter in the Keys tab, whose combo
        // already offers "(off)" as an item; J is simply what it used to be and is still free.
        new(PasteAction.ToggleJoinMark, "join", "Mark the clip to be pasted joined with the others", null),
        new(PasteAction.PromoteToFront, "front", "Move the clip to the front of the stack", 'M', "Q"),
        new(PasteAction.CycleFormatter, "format", "Cycle the paste format", 'Z'),
        new(PasteAction.CycleKindFilter, "kind", "Show only one kind of clip: all, text, images, files", 'K'),
        new(PasteAction.EditTags, "tags", "Edit tags", 'T'),
        new(PasteAction.PushToClipboard, "clipboard", "Put the clip on the clipboard without pasting", 'S'),
        new(PasteAction.EditClip, "editor", "Open the clip in an external editor", 'O'),
        new(PasteAction.ShowHistory, "history", "Open the clipboard history window", 'H'),
        new(PasteAction.ExportClip, "export", "Export the clip to a file", 'E'),
        new(PasteAction.CycleCommitMode, "commit", "Cycle what releasing Ctrl will do", 'X'),
    ];

    /// <summary>
    /// <b>Every</b> key that cannot be moved, and what it does.
    /// <para>
    /// The complete list, deliberately - including the keys that also appear as an "also ..." note beside a
    /// letter's row. The first attempt listed only the actions with no letter of their own, which left
    /// <c>Home</c>, <c>Space</c>, <c>Q</c> and the arrows out of a block headed "these cannot be changed" while
    /// they cannot be changed either. Anyone reading it to learn what is fixed got a wrong answer, and it was
    /// reported as such. The duplication with those notes is the point: one says "what else fires this action",
    /// this says "what no set of bindings can take away".
    /// </para>
    /// <para>
    /// It also caught three that were documented nowhere at all: the numpad digits, numpad minus, and
    /// <c>Backspace</c> in the search box.
    /// </para>
    /// <para>
    /// That these cannot move is the safety property the whole design rests on - see the note on this class.
    /// <c>Esc</c> above all: a session that could not be cancelled would be a dead keyboard.
    /// </para>
    /// <para>
    /// Display text rather than key codes, because <c>Core</c> has no business knowing Windows virtual keys.
    /// <c>VirtualKeyTranslatorTests</c> checks that each of these really does fire something, and that the count
    /// here matches what it knows about - so a key documented here but never wired up fails a test rather than a
    /// user.
    /// </para>
    /// </summary>
    public static IReadOnlyList<(string Keys, string Description)> FixedActions { get; } =
    [
        ("Down / Right", "Step to an older clip"),
        ("Up / Left", "Step back to a newer clip"),
        ("Home", "Jump to the newest clip"),
        ("End", "Jump to the oldest clip"),
        ("1 - 9", "Jump that many clips at once, numpad included"),
        ("-", "Reverse the direction the number keys jump in, numpad included"),
        ("Space", "Pin or unpin the clip"),
        ("Q", "Move the clip to the front of the stack"),
        ("Delete", "Delete this clip now and carry on browsing"),
        ("Enter", "Paste and stay open, to paste several clips in a row"),
        ("Backspace", "Delete a character while searching"),
        ("Shift", "Hold it, then release Ctrl, to delete the clip after pasting"),
        ("Esc", "Cancel and restore the previous clipboard"),
        ("F1", "Show the key list"),
    ];

    /// <summary>
    /// Letters that fire an action, indexed by <c>letter - 'A'</c>. A 26-entry array rather than a dictionary
    /// because this is read inside the keyboard hook, once per keystroke, where the callback blocks all input
    /// on the machine until it returns.
    /// </summary>
    private readonly GestureKey[] _byLetter = new GestureKey[26];

    private readonly Dictionary<string, char?> _letters = [];

    private PasteKeyMap()
    {
    }

    /// <summary>The bindings a fresh install has.</summary>
    public static PasteKeyMap Default { get; } = Parse(null);

    /// <summary>
    /// The gesture key a letter fires, or <see cref="GestureKey.None"/> when it fires nothing - which is what
    /// lets an unbound letter fall through to the search box.
    /// </summary>
    public GestureKey ForLetter(char letter)
    {
        var index = char.ToUpperInvariant(letter) - 'A';

        return index is >= 0 and < 26 ? _byLetter[index] : GestureKey.None;
    }

    /// <summary>The letter bound to an action, or null when the action is switched off.</summary>
    public char? LetterFor(string name) => _letters.TryGetValue(name, out var letter) ? letter : null;

    /// <summary>Whether an action is enabled at all.</summary>
    public bool IsEnabled(string name) => LetterFor(name) is not null;

    /// <summary>
    /// Every letter this map claims. What <c>TriggerKey</c> consults, so the trigger cannot be given a letter
    /// that already does something - which would shadow that action for ever.
    /// </summary>
    public IReadOnlyDictionary<char, string> ClaimedLetters()
    {
        var claimed = new Dictionary<char, string>();

        foreach (var entry in Entries)
        {
            if (LetterFor(entry.Name) is { } letter)
            {
                claimed[letter] = entry.Description;
            }

            // A fixed alias that happens to be a letter is claimed just as firmly as the configurable one: Q
            // still moves a clip to the front, so a trigger on Q would steal it. Multi-character aliases like
            // "Up / Left" are not letters and contribute nothing here.
            if (entry.FixedAlias is { Length: 1 } alias && char.IsAsciiLetter(alias[0]))
            {
                claimed[char.ToUpperInvariant(alias[0])] = entry.Description;
            }
        }

        return claimed;
    }

    /// <summary>
    /// Reads the settings string. Tolerant by design: an unknown name is ignored, a missing one falls back to
    /// its default, and anything unparseable leaves that action at its default rather than unbinding it.
    /// <para>
    /// Format is <c>name=letter</c> pairs separated by semicolons, with an empty letter meaning "switched off":
    /// <c>back=C;newest=A;format=;pin=K</c>. A string rather than a dictionary because settings are compared by
    /// value to decide whether a row differs from its default, and because it has to read sensibly on the
    /// Advanced tab.
    /// </para>
    /// <para>
    /// Silence on bad input is deliberate here, unlike most of this codebase: this runs during start-up before
    /// there is a window to report in, and refusing to start because one letter in a hand-edited file was wrong
    /// would be far worse than quietly using the default. <see cref="Validate"/> is where a bad set is refused,
    /// and it runs against what the user typed in the dialog.
    /// </para>
    /// </summary>
    public static PasteKeyMap Parse(string? stored)
    {
        var map = new PasteKeyMap();

        foreach (var entry in Entries)
        {
            map._letters[entry.Name] = entry.DefaultLetter;
        }

        // Which actions the stored string actually named. Tracked because a saved binding must beat a default -
        // see the loop after this one.
        var explicitNames = new HashSet<string>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(stored))
        {
            foreach (var pair in stored.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var split = pair.IndexOf('=');

                if (split <= 0)
                {
                    continue;
                }

                var name = pair[..split].Trim();
                var value = pair[(split + 1)..].Trim();

                if (!map._letters.ContainsKey(name))
                {
                    continue;
                }

                map._letters[name] = value.Length == 1 && char.IsAsciiLetter(value[0])
                    ? char.ToUpperInvariant(value[0])
                    : value.Length == 0 ? null : map._letters[name];

                explicitNames.Add(name);
            }
        }

        // An action the user has bound wins over one that merely defaults to the same letter, and the defaulted
        // action is left unbound rather than allowed to steal it.
        //
        // This exists because of what happens when an action is ADDED. "Mark to join" arrived with a default of J,
        // which was free - but free is not the same as unused: anyone who had moved pin to J would have had it
        // silently taken away, since Rebuild lets the later entry win. Nothing that works may stop working, so the
        // new action starts off instead, visible as "(off)" in the Keys tab where it can be given a free letter.
        //
        // Join itself no longer needs this - it ships with no letter at all now - but the next added action will,
        // and this is the only thing standing between it and someone's existing configuration.
        //
        // Only when there IS a stored string: a fresh install has no explicit bindings and its defaults do not
        // clash with each other.
        if (explicitNames.Count > 0)
        {
            var claimed = new HashSet<char>(
                Entries
                    .Where(entry => explicitNames.Contains(entry.Name))
                    .Select(entry => map._letters[entry.Name])
                    .OfType<char>());

            foreach (var entry in Entries)
            {
                if (!explicitNames.Contains(entry.Name)
                    && map._letters[entry.Name] is { } letter
                    && claimed.Contains(letter))
                {
                    map._letters[entry.Name] = null;
                }
            }
        }

        map.Rebuild();
        return map;
    }

    /// <summary>Builds a map from an explicit set of choices, for the settings dialog.</summary>
    public static PasteKeyMap FromChoices(IReadOnlyDictionary<string, char?> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);

        var map = new PasteKeyMap();

        foreach (var entry in Entries)
        {
            map._letters[entry.Name] = choices.TryGetValue(entry.Name, out var letter)
                ? letter is { } c ? char.ToUpperInvariant(c) : null
                : entry.DefaultLetter;
        }

        map.Rebuild();
        return map;
    }

    /// <summary>
    /// Why a set of choices cannot be used, or null when it can.
    /// <para>
    /// A duplicate is the case that matters. Two actions on one letter is not a preference the code could honour
    /// half of - whichever the lookup happened to write last would win, silently - so it is refused with the
    /// clash named rather than resolved.
    /// </para>
    /// </summary>
    public static string? Validate(IReadOnlyDictionary<string, char?> choices, char triggerLetter)
    {
        ArgumentNullException.ThrowIfNull(choices);

        var seen = new Dictionary<char, string>();
        var trigger = char.ToUpperInvariant(triggerLetter);

        foreach (var entry in Entries)
        {
            if (!choices.TryGetValue(entry.Name, out var maybe) || maybe is not { } raw)
            {
                continue;
            }

            var letter = char.ToUpperInvariant(raw);

            if (!char.IsAsciiLetterUpper(letter))
            {
                return $"\"{entry.Description}\" needs a letter from A to Z.";
            }

            if (letter == trigger)
            {
                return $"{letter} opens paste mode, so it cannot also be \"{entry.Description}\".";
            }

            if (seen.TryGetValue(letter, out var other))
            {
                return $"{letter} is set for both \"{other}\" and \"{entry.Description}\".";
            }

            seen[letter] = entry.Description;

            // Checked against the fixed aliases too, since those fire whatever the letter says. Binding tags to
            // Q would mean Q did two things, with the fixed alias winning or losing depending on lookup order.
            foreach (var candidate in Entries)
            {
                if (candidate.FixedAlias is { Length: 1 } alias
                    && char.ToUpperInvariant(alias[0]) == letter
                    && candidate.Name != entry.Name)
                {
                    return $"{letter} already does \"{candidate.Description}\" and cannot be moved.";
                }
            }
        }

        return null;
    }

    /// <summary>Renders back to the settings string, omitting nothing so the file says what is in force.</summary>
    public string ToSettingsString()
    {
        var builder = new StringBuilder();

        foreach (var entry in Entries)
        {
            if (builder.Length > 0)
            {
                builder.Append(';');
            }

            builder.Append(entry.Name).Append('=').Append(LetterFor(entry.Name));
        }

        return builder.ToString();
    }

    private void Rebuild()
    {
        Array.Clear(_byLetter);

        foreach (var entry in Entries)
        {
            if (_letters[entry.Name] is { } letter && char.IsAsciiLetterUpper(letter))
            {
                _byLetter[letter - 'A'] = ToGestureKey(entry.Action);
            }

            // The fixed alias, when it is a letter. Applied after the configurable one so a clash cannot silently
            // disable the alias - Validate refuses that case before it can be saved.
            if (entry.FixedAlias is { Length: 1 } aliasText && char.IsAsciiLetter(aliasText[0]))
            {
                var alias = char.ToUpperInvariant(aliasText[0]);

                if (_byLetter[alias - 'A'] == GestureKey.None)
                {
                    _byLetter[alias - 'A'] = ToGestureKey(entry.Action);
                }
            }
        }
    }

    /// <summary>
    /// The gesture key that carries an action. The inverse of <c>GestureKeyExtensions.ToAction</c>, and it is
    /// spelled out rather than searched for so a new action fails to compile here instead of silently mapping to
    /// nothing.
    /// </summary>
    private static GestureKey ToGestureKey(PasteAction action) => action switch
    {
        PasteAction.Back => GestureKey.Back,
        PasteAction.JumpToNewest => GestureKey.JumpToNewest,
        PasteAction.ToggleSearch => GestureKey.ToggleSearch,
        PasteAction.TogglePin => GestureKey.TogglePin,
        PasteAction.ToggleJoinMark => GestureKey.ToggleJoinMark,
        PasteAction.PromoteToFront => GestureKey.PromoteToFront,
        PasteAction.CycleFormatter => GestureKey.CycleFormatter,
        PasteAction.CycleKindFilter => GestureKey.CycleKindFilter,
        PasteAction.EditTags => GestureKey.EditTags,
        PasteAction.PushToClipboard => GestureKey.PushToClipboard,
        PasteAction.EditClip => GestureKey.EditClip,
        PasteAction.ShowHistory => GestureKey.ShowHistory,
        PasteAction.ExportClip => GestureKey.ExportClip,
        PasteAction.CycleCommitMode => GestureKey.CycleCommitMode,
        _ => GestureKey.None,
    };
}
