using System.Text;
using EconomyBot.Worker.Models;
using TL;
using ModelUser = EconomyBot.Worker.Models.PeerUser;

namespace EconomyBot.Worker.Features;

/// <summary>
/// Helpers for building Telegram inline user-mention messages via MTProto.
///
/// Why not tg://user?id= ?
/// MarkdownToEntities converts that link into InputMessageEntityMentionName
/// with InputUser(id, accessHash:0). Telegram's server cannot resolve a user
/// without the correct access hash, so the mention silently fails.
///
/// The correct approach is to build InputMessageEntityMentionName manually
/// using the access hash we already store in PeerUser.AccessHash.
/// </summary>
public static class MentionHelper
{
    private const string Fallback = "Unknown";

    // ── Primitives ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the display label and an <see cref="InputMessageEntityMentionName"/>
    /// for the given user. If the user is null or has no name the label is "Unknown"
    /// and the entity is null (no mention produced).
    /// </summary>
    public static (string label, InputMessageEntityMentionName? entity) Mention(ModelUser? user)
    {
        var name  = user?.GetFullName();
        var label = string.IsNullOrWhiteSpace(name) ? Fallback : name!;

        if (user is null || user.UserId == 0)
            return (label, null);

        var entity = new InputMessageEntityMentionName
        {
            // offset / length are filled in by Build()
            user_id = new InputUser(user.UserId, user.AccessHash)
        };

        return (label, entity);
    }

    // ── Builder ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Assembles a final message string and its <see cref="MessageEntity"/> array.
    ///
    /// Usage:
    /// <code>
    ///   var (text, entities) = MentionHelper.Build(
    ///       "Hello {0}, you beat {1}!",
    ///       MentionHelper.Mention(player1),
    ///       MentionHelper.Mention(player2));
    ///
    ///   await client.Messages_SendMessage(peer, text,
    ///       random_id: Helpers.RandomLong(), entities: entities);
    /// </code>
    ///
    /// Placeholders <c>{0}</c>, <c>{1}</c>, … are replaced with the corresponding
    /// label; each non-null entity gets its <c>offset</c> and <c>length</c> set
    /// from the actual position in the assembled string.
    ///
    /// Pass plain text tokens as <c>(label, null)</c> to suppress the entity:
    /// <c>MentionHelper.Build("Invalid word: {0}", ("cat", null))</c>
    /// </summary>
    public static (string text, MessageEntity[] entities) Build(
        string template,
        params (string label, InputMessageEntityMentionName? entity)[] mentions)
    {
        return BuildWithEntities(template, null, mentions);
    }

    public static (string text, MessageEntity[] entities) BuildWithEntities(
        string template,
        MessageEntity[]? existingEntities,
        params (string label, InputMessageEntityMentionName? entity)[] mentions)
    {
        var sb       = new StringBuilder();
        var entities = new List<MessageEntity>();
        if (existingEntities != null)
            entities.AddRange(existingEntities);

        var remaining = template;
        int currentTemplateIndex = 0;
        int currentOutputIndex = 0;

        for (int i = 0; i < mentions.Length; i++)
        {
            var token = $"{{{i}}}";
            var idx   = remaining.IndexOf(token, StringComparison.Ordinal);
            if (idx < 0) break;

            int prefixLen = idx;
            sb.Append(remaining[..idx]);
            currentTemplateIndex += prefixLen;
            currentOutputIndex += prefixLen;

            var (label, entity) = mentions[i];
            
            // Adjust existing entities that are AFTER this token
            int shift = label.Length - token.Length;
            if (shift != 0 && existingEntities != null)
            {
                foreach (var ex in existingEntities)
                {
                    if (ex.offset >= currentTemplateIndex + token.Length)
                    {
                        ex.offset += shift;
                    }
                    else if (ex.offset < currentTemplateIndex + token.Length && ex.offset + ex.length > currentTemplateIndex)
                    {
                        ex.length += shift; // if the entity envelops the token
                    }
                }
            }

            int offset = currentOutputIndex;
            sb.Append(label);
            currentTemplateIndex += token.Length;
            currentOutputIndex += label.Length;

            if (entity is not null)
            {
                entity.offset = offset;
                entity.length = label.Length;
                entities.Add(entity);
            }

            remaining = remaining[(idx + token.Length)..];
        }

        sb.Append(remaining);
        return (sb.ToString(), entities.ToArray());
    }

    /// <summary>
    /// Convenience overload: produces a plain-text token (no mention entity).
    /// </summary>
    public static (string label, InputMessageEntityMentionName? entity) Plain(string text)
        => (text, null);
}
