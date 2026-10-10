using System.Security.Cryptography;
using System.Text.Json;
using Lane.Core.Identity;
using Lane.Core.Messages;
using Lane.Core.Models;
using Lane.Core.Tools;
using Lane.Node.Sdk;
using Lane.Nodes.Protocol;
using Xunit;

namespace Lane.Tests;

public sealed class NodeProtocolTests
{
    [Fact]
    public void Request_round_trips_through_the_wire_format()
    {
        SurfaceId surface = new("api");
        SessionId session = new(surface, SessionKind.Text, "abc");
        Participant author = new(new ParticipantId(surface, "u1"), "Someone", "someone");

        ModelRequest request = new()
        {
            System   = [new PromptBlock("You are Lane.", CacheHint.Persistent)],
            Messages =
            [
                LaneMessage.User(session, author,
                    [new TextPart("look"), new ImagePart(null, new byte[] { 1, 2, 3 }, "image/png"),
                     new AudioPart(new byte[] { 4, 5, 6 }, "wav")],
                    DateTimeOffset.UnixEpoch),
                LaneMessage.Assistant(session, Participant.Lane(surface),
                    [new ToolUsePart("call-1", "web_search", JsonDocument.Parse("""{"q":"x"}""").RootElement)],
                    DateTimeOffset.UnixEpoch)
            ],
            Tools =
            [
                new ToolDescriptor
                {
                    Name        = "web_search",
                    Description = "Search",
                    InputSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement
                }
            ],
            ToolChoice     = ToolChoice.Specific("web_search"),
            ResponseFormat = new ResponseFormat("out", JsonDocument.Parse("""{"type":"object"}""").RootElement),
            StopSequences  = ["END"]
        };

        string json = JsonSerializer.Serialize<NodeMessage>(new NodeRequest("r1", request), NodeProtocol.Json);

        NodeRequest back = Assert.IsType<NodeRequest>(JsonSerializer.Deserialize<NodeMessage>(json, NodeProtocol.Json));

        Assert.Equal("r1", back.RequestId);
        Assert.Equal(CacheHint.Persistent, back.Request.System[0].Cache);
        Assert.Equal(session, back.Request.Messages[0].Session);
        Assert.Equal(author, back.Request.Messages[0].Author);

        ImagePart image = Assert.IsType<ImagePart>(back.Request.Messages[0].Content[1]);
        Assert.Equal(new byte[] { 1, 2, 3 }, image.Data!.Value.ToArray());

        AudioPart audio = Assert.IsType<AudioPart>(back.Request.Messages[0].Content[2]);
        Assert.Equal(new byte[] { 4, 5, 6 }, audio.Data.ToArray());
        Assert.Equal("wav", audio.Format);

        ToolUsePart call = Assert.IsType<ToolUsePart>(back.Request.Messages[1].Content[0]);
        Assert.Equal("x", call.Arguments.GetProperty("q").GetString());

        Assert.Equal("web_search", back.Request.Tools[0].Name);
        Assert.Equal(ToolChoiceMode.Specific, back.Request.ToolChoice.Mode);
        Assert.Equal("out", back.Request.ResponseFormat!.Name);
        Assert.Equal(["END"], back.Request.StopSequences!);
    }

    [Fact]
    public void Signature_survives_the_reply_being_serialized_and_parsed()
    {
        using FileNodeKey key = FileNodeKey.Generate();

        ModelResponse response = new(
            [new TextPart("hello"), new ToolUsePart("c", "t", JsonDocument.Parse("""{ "a" : 1.50 }""").RootElement)],
            StopReason.ToolUse,
            new TokenUsage(10, 2, 1, 0, "node", TimeSpan.FromMilliseconds(123.4567)));

        string signature = Convert.ToBase64String(key.Sign(NodeProtocol.SigningPayload("r1", response)));

        string json = JsonSerializer.Serialize<NodeMessage>(new NodeReply("r1", response, signature), NodeProtocol.Json);
        NodeReply back = Assert.IsType<NodeReply>(JsonSerializer.Deserialize<NodeMessage>(json, NodeProtocol.Json));

        NodeAttestation attestation = new(key.Identity, back.Signature);

        Assert.True(NodeProtocol.Verify(attestation, back.RequestId, back.Response));
        Assert.False(NodeProtocol.Verify(attestation, "r2", back.Response));
    }

    [Fact]
    public void Security_key_vouches_for_the_session_key_that_signs_replies()
    {
        using FakeSecurityKey securityKey = new();
        using WebAuthnSession session = new();
        using WebAuthnNodeKey key = securityKey.Vouch(session);

        Assert.Equal(NodeProtocol.WebAuthnEs256, key.Identity.Algorithm);
        Assert.Equal(NodeProtocol.IdentityFor(securityKey.PublicKey, NodeProtocol.WebAuthnEs256), key.Identity);

        ModelResponse response = new([new TextPart("hi")], StopReason.EndTurn, default);
        string signature = Convert.ToBase64String(key.Sign(NodeProtocol.SigningPayload("r1", response)));

        Assert.True(NodeProtocol.Verify(new NodeAttestation(key.Identity, signature, key.Delegation), "r1", response));

        Assert.False(NodeProtocol.Verify(new NodeAttestation(key.Identity, signature), "r1", response));

        using FileNodeKey other = FileNodeKey.Generate();
        NodeDelegation swapped = key.Delegation with { SessionPublicKey = other.Identity.PublicKey };
        string otherSignature = Convert.ToBase64String(other.Sign(NodeProtocol.SigningPayload("r1", response)));

        Assert.False(NodeProtocol.Verify(new NodeAttestation(key.Identity, otherSignature, swapped), "r1", response));
    }

    [Fact]
    public void Session_rejects_assertions_that_are_not_for_it()
    {
        using FakeSecurityKey securityKey = new();
        using WebAuthnSession session = new();
        using WebAuthnSession unrelated = new();

        Assert.Throws<ArgumentException>(() => securityKey.Vouch(session, challenge: unrelated.Challenge));
        Assert.Throws<ArgumentException>(() => securityKey.Vouch(session, userPresent: false));

        using FakeSecurityKey impostor = new();
        (byte[] data, byte[] client, byte[] signature) = impostor.Assert(session.Challenge);
        Assert.Throws<ArgumentException>(() => session.Complete(securityKey.PublicKey, data, client, signature));

        using WebAuthnNodeKey key = securityKey.Vouch(session);
        Assert.Throws<InvalidOperationException>(() => securityKey.Vouch(session));
    }

    [Fact]
    public void Only_p256_credential_keys_are_supported()
    {
        using ECDsa p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using RSA rsa    = RSA.Create(2048);

        Assert.True(WebAuthnSession.IsSupportedCredentialKey(p256.ExportSubjectPublicKeyInfo()));
        Assert.False(WebAuthnSession.IsSupportedCredentialKey(p384.ExportSubjectPublicKeyInfo()));
        Assert.False(WebAuthnSession.IsSupportedCredentialKey(rsa.ExportSubjectPublicKeyInfo()));
        Assert.False(WebAuthnSession.IsSupportedCredentialKey([1, 2, 3]));
    }

    [Fact]
    public void Key_id_is_derived_from_the_public_key()
    {
        using FileNodeKey key = FileNodeKey.Generate();

        NodeIdentity again = NodeProtocol.IdentityFor(Convert.FromBase64String(key.Identity.PublicKey));

        Assert.Equal(key.Identity, again);
        Assert.Equal(32, key.Identity.KeyId.Length);
    }

    [Theory]
    [InlineData("ws://lane:5070",       "ws://lane:5070/v1/nodes/connect")]
    [InlineData("http://lane:5070/",    "ws://lane:5070/v1/nodes/connect")]
    [InlineData("https://lane.example", "wss://lane.example/v1/nodes/connect")]
    [InlineData("ws://lane:5070/custom", "ws://lane:5070/custom")]
    public void Connect_uri_normalises_scheme_and_path(string input, string expected) =>
        Assert.Equal(new Uri(expected), LaneNode.ConnectUri(input));
}
