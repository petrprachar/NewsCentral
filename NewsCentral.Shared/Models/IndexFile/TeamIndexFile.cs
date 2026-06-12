using System.Text.Json.Serialization;
using NewsCentral.Security;

namespace NewsCentral.Models.IndexFile
{
    public class TeamIndexFile : ISignable, IDeliveredKeyCarrier
    {
        public string TeamFolderName { get; set; } = string.Empty;
        public string TeamName { get; set; } = string.Empty;
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
        public string Version { get; set; } = "1.0.0";
        public string IndexHash { get; set; } = string.Empty;
        public List<PublishedAssignmentIndex> PublishedAssignments { get; set; } = new();
        public IndexStatistics Statistics { get; set; } = new();
        public string? Signature { get; set; }

        /// <summary>
        /// Per-team ECDSA public key (same Base64 SubjectPublicKeyInfo form as the registry
        /// PublicKey values) delivered with the index for the key-with-content model. It is
        /// excluded from the canonical signing input — exactly like <see cref="Signature"/> —
        /// so the signature does not cover this field. <see cref="JsonIgnoreCondition.WhenWritingNull"/>
        /// keeps the canonical bytes bit-identical to the pre-key form when the value is null,
        /// so every pre-existing static-team signature still verifies.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SigningPublicKey { get; set; }
    }
}
