namespace Roomie.App.Services;

/// <summary>An emoji to choose, with the name a screen reader says for it.</summary>
public sealed record EmojiOption(string Glyph, string Name)
{
    /// <summary>What a screen reader says for this row. Without it, the record's generated text is read out, IDs and all.</summary>
    public override string ToString() => Name;
}

/// <summary>
/// The emoji offered next to the message box. All are single characters from Unicode 11 or earlier, so Windows 10 can
/// draw each one on its own. Sent as ordinary text, so they need nothing from the other computer's Roomie version.
/// </summary>
public static class EmojiCatalog
{
    public static IReadOnlyList<EmojiOption> All { get; } =
    [
        new("😀", "Grinning face"),
        new("😂", "Face with tears of joy"),
        new("🤣", "Rolling on the floor laughing"),
        new("😊", "Smiling face"),
        new("😍", "Heart eyes"),
        new("😘", "Blowing a kiss"),
        new("😉", "Winking face"),
        new("😎", "Sunglasses"),
        new("🤔", "Thinking face"),
        new("😐", "Neutral face"),
        new("🙄", "Rolling eyes"),
        new("😴", "Sleeping face"),
        new("😅", "Relieved laugh"),
        new("😢", "Crying face"),
        new("😭", "Loudly crying face"),
        new("😡", "Angry face"),
        new("😱", "Screaming face"),
        new("🤯", "Mind blown"),
        new("🥳", "Party face"),
        new("🤗", "Hugging face"),
        new("👍", "Thumbs up"),
        new("👎", "Thumbs down"),
        new("👌", "OK hand"),
        new("👏", "Clapping hands"),
        new("🙏", "Thank you"),
        new("💪", "Flexed arm"),
        new("👋", "Waving hand"),
        new("🤝", "Handshake"),
        new("🎉", "Party popper"),
        new("🔥", "Fire"),
        new("✨", "Sparkles"),
        new("💯", "Hundred points"),
        new("💔", "Broken heart"),
        new("💙", "Blue heart"),
        new("✅", "Check mark"),
        new("❌", "Cross mark"),
        new("⏳", "Hourglass"),
        new("📁", "Folder"),
        new("📎", "Paperclip"),
        new("💻", "Laptop"),
        new("🏠", "House"),
        new("🍕", "Pizza"),
        new("☕", "Coffee"),
        new("🍺", "Beer"),
        new("🎮", "Video game"),
        new("🎵", "Music note"),
        new("🐱", "Cat"),
        new("🐶", "Dog"),
    ];
}
