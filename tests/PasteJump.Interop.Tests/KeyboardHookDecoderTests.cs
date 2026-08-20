using PasteJump.Interop;
using Xunit;

namespace PasteJump.Interop.Tests;

/// <summary>
/// Tests the decisions the keyboard hook callback makes, which until now had none: installing a real
/// <c>WH_KEYBOARD_LL</c> hook needs a message loop and a live keyboard, so the callback and everything decided
/// inside it sat in the one part of the application nothing could reach.
/// </summary>
/// <remarks>
/// Every value here is written as a literal with the name in a comment, deliberately not taken from
/// <c>NativeConstants</c>. Those are Windows' numbers from WinUser.h, so stating them independently means a typo
/// in the constants table fails a test instead of agreeing with itself.
/// </remarks>
public class KeyboardHookDecoderTests
{
    private const int HcAction = 0;         // HC_ACTION
    private const int WmKeyDown = 0x0100;   // WM_KEYDOWN
    private const int WmKeyUp = 0x0101;     // WM_KEYUP
    private const int WmSysKeyDown = 0x0104; // WM_SYSKEYDOWN
    private const int WmSysKeyUp = 0x0105;  // WM_SYSKEYUP
    private const uint Injected = 0x0010;   // LLKHF_INJECTED
    private const int VkV = 0x56;           // 'V'

    private static readonly IntPtr OurSignature = new(0x436A6F67);

    private static KeyboardHookEvent? Decode(
        int message,
        uint flags = 0,
        IntPtr extraInfo = default,
        int code = HcAction,
        int virtualKey = VkV)
        => KeyboardHookDecoder.Decode(code, message, virtualKey, flags, extraInfo, OurSignature);

    // ------------------------------------------------------------------ what counts as an event

    [Theory]
    [InlineData(WmKeyDown, true)]
    [InlineData(WmSysKeyDown, true)]
    [InlineData(WmKeyUp, false)]
    [InlineData(WmSysKeyUp, false)]
    public void The_four_key_messages_all_produce_an_event(int message, bool expectedKeyDown)
    {
        var decoded = Decode(message);

        Assert.NotNull(decoded);
        Assert.Equal(expectedKeyDown, decoded!.Value.IsKeyDown);
        Assert.Equal(VkV, decoded.Value.VirtualKey);
    }

    /// <summary>
    /// The Sys variants are not an edge case: <c>WM_SYSKEYDOWN</c> is what arrives for every keystroke made while
    /// Alt is held. Handling only <c>WM_KEYDOWN</c> would make every Alt chord invisible to the recognizer - and
    /// Alt chords are precisely what it must see in order to decline to swallow them, so Alt+Tab keeps working.
    /// </summary>
    [Fact]
    public void An_Alt_chord_is_seen_because_the_Sys_messages_are_handled()
    {
        Assert.NotNull(Decode(WmSysKeyDown, virtualKey: 0x09)); // Tab
        Assert.NotNull(Decode(WmSysKeyUp, virtualKey: 0x09));
    }

    [Theory]
    [InlineData(0x0106)]      // WM_SYSCHAR - a message we do not handle
    [InlineData(0x0102)]      // WM_CHAR
    [InlineData(0x0000)]
    public void A_message_that_is_not_a_key_transition_is_not_ours(int message)
        => Assert.Null(Decode(message));

    /// <summary>
    /// Any <c>nCode</c> but <c>HC_ACTION</c> means the parameters carry no event, and Windows requires them to be
    /// passed along uninterpreted. Reading them would be reading whatever happened to be in memory.
    /// </summary>
    [Theory]
    [InlineData(3)]   // HC_NOREMOVE
    [InlineData(-1)]
    [InlineData(1)]
    public void Anything_but_HC_ACTION_carries_no_event(int code)
        => Assert.Null(Decode(WmKeyDown, code: code));

    // ------------------------------------------------------------------ the injection rules

    /// <summary>
    /// The landmine, as a test. <c>LLKHF_INJECTED</c> is set by <em>any</em> process calling <c>SendInput</c>, so
    /// treating it as "ours" killed the gesture outright under Remote Desktop, in VM guest windows, and for anyone
    /// on a macro keyboard, an on-screen keyboard or an accessibility tool. Injected-by-somebody-else must arrive
    /// as an ordinary keystroke.
    /// </summary>
    [Fact]
    public void Somebody_elses_injected_key_is_injected_but_not_ours()
    {
        var decoded = Decode(WmKeyDown, flags: Injected, extraInfo: new IntPtr(0x1234));

        Assert.NotNull(decoded);
        Assert.True(decoded!.Value.IsInjected);
        Assert.False(decoded.Value.IsOwnInjection);
    }

    /// <summary>
    /// And the loop guard that the signature exists for: without recognising our own injection, sending Ctrl+V to
    /// perform the paste re-enters paste mode for ever.
    /// </summary>
    [Fact]
    public void Our_own_injected_key_is_recognised_by_its_signature()
    {
        var decoded = Decode(WmKeyDown, flags: Injected, extraInfo: OurSignature);

        Assert.NotNull(decoded);
        Assert.True(decoded!.Value.IsInjected);
        Assert.True(decoded.Value.IsOwnInjection);
    }

    /// <summary>
    /// A physical keystroke carries no injected flag, whatever else is in <c>dwExtraInfo</c> - drivers and other
    /// software do put values there. Ownership requires the flag as well as the signature, or a hardware key
    /// could be mistaken for our own paste and silently dropped.
    /// </summary>
    [Fact]
    public void A_physical_key_is_never_our_injection_even_carrying_our_signature()
    {
        var decoded = Decode(WmKeyDown, flags: 0, extraInfo: OurSignature);

        Assert.NotNull(decoded);
        Assert.False(decoded!.Value.IsInjected);
        Assert.False(decoded.Value.IsOwnInjection);
    }

    [Fact]
    public void A_physical_key_is_the_ordinary_case()
    {
        var decoded = Decode(WmKeyDown);

        Assert.NotNull(decoded);
        Assert.False(decoded!.Value.IsInjected);
        Assert.False(decoded.Value.IsOwnInjection);
    }

    /// <summary>
    /// Other flags in the field must not be read as the injected bit. <c>LLKHF_EXTENDED</c> (0x01),
    /// <c>LLKHF_LOWER_IL_INJECTED</c> (0x02) and <c>LLKHF_ALTDOWN</c> (0x20) all travel in the same field.
    /// </summary>
    [Theory]
    [InlineData(0x01u)]  // LLKHF_EXTENDED - an extended key such as the right-hand Ctrl
    [InlineData(0x20u)]  // LLKHF_ALTDOWN
    [InlineData(0x80u)]  // LLKHF_UP
    [InlineData(0x21u)]
    public void A_neighbouring_flag_is_not_the_injected_flag(uint flags)
    {
        var decoded = Decode(WmKeyDown, flags: flags);

        Assert.NotNull(decoded);
        Assert.False(decoded!.Value.IsInjected);
    }

    /// <summary>
    /// <c>LLKHF_LOWER_IL_INJECTED</c> means injected by a lower-integrity process, and it arrives <em>with</em>
    /// the injected flag. It is somebody else's input either way.
    /// </summary>
    [Fact]
    public void Injected_from_a_lower_integrity_process_is_still_not_ours()
    {
        var decoded = Decode(WmKeyDown, flags: Injected | 0x02u, extraInfo: OurSignature - 1);

        Assert.NotNull(decoded);
        Assert.True(decoded!.Value.IsInjected);
        Assert.False(decoded.Value.IsOwnInjection);
    }

    // ------------------------------------------------------------------ the key itself

    /// <summary>
    /// The virtual key is passed through untouched across the whole range, because the recognizer's own sweep
    /// over all 256 keys is only meaningful if this layer reports what Windows said.
    /// </summary>
    [Fact]
    public void Every_virtual_key_is_reported_as_given()
    {
        for (var vk = 0; vk <= 255; vk++)
        {
            var decoded = Decode(WmKeyDown, virtualKey: vk);

            Assert.NotNull(decoded);
            Assert.Equal(vk, decoded!.Value.VirtualKey);
        }
    }
}
