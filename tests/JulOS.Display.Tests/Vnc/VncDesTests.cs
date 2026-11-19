using JulOS.Integrations.Display.Vnc;

namespace JulOS.Integrations.Display.Tests.Vnc;

/// <summary>The DES of the VNC password sign-in, against published vectors and against OpenSSL.</summary>
public sealed class VncDesTests
{
    [Theory]
    [InlineData("133457799BBCDFF1", "0123456789ABCDEF", "85E813540F0AB405")]   // the worked example everybody uses
    [InlineData("0E329232EA6D0D73", "8787878787878787", "0000000000000000")]   // from the NBS validation set
    [InlineData("0101010101010101", "8000000000000000", "95F8A5E5DD31D900")]   // variable plaintext known answer
    public void Plain_des_gives_the_published_ciphertext(string key, string plain, string expected)
    {
        var output = new byte[8];
        VncDes.EncryptBlock(Convert.FromHexString(key), Convert.FromHexString(plain), output);
        Assert.Equal(expected, Convert.ToHexString(output));
    }

    [Theory]
    [InlineData("password", "94E99E3A43013075D70943A454976A95")]
    [InlineData("passwordXYZ", "94E99E3A43013075D70943A454976A95")]       // only eight characters count
    [InlineData("abc", "5D30C09D600699FA6C8F4EE5F8FBF50B")]                // shorter: padded with zero bytes
    public void The_response_is_des_with_a_bit_reversed_key(string password, string expected)
    {
        // The expected values were made with OpenSSL (des-ecb, key = the password's bytes, each with its bits reversed) for the challenge 10 11 ... 1F
        var challenge = Convert.FromHexString("101112131415161718191A1B1C1D1E1F");
        Assert.Equal(expected, Convert.ToHexString(VncDes.Response(password, challenge)));
    }

    [Fact]
    public void A_challenge_is_sixteen_bytes()
    {
        Assert.Throws<ArgumentException>(() => VncDes.Response("x", new byte[8]));
    }
}
