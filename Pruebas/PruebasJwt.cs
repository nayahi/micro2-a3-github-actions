using ApiGateway.BFF.Seguridad;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using System.Text;

namespace ECommerceGRPC.Pruebas;

// Asincrónico 3 · Esta prueba solo pasa si el repositorio tiene el secreto JWT_CLAVE
// (Settings → Secrets and variables → Actions). El workflow lo pasa como variable de entorno.
// En su máquina: $env:JWT_CLAVE = "<una clave de 32 caracteres o más>"; dotnet test
public class PruebasJwt
{
    [Fact]
    public async Task El_gateway_firma_y_valida_un_token_con_el_secreto_JWT_CLAVE()
    {
        var clave = Environment.GetEnvironmentVariable("JWT_CLAVE");
        Assert.False(string.IsNullOrWhiteSpace(clave),
            "Falta el secreto JWT_CLAVE. Créelo en Settings → Secrets and variables → Actions → New repository secret.");
        Assert.True(clave!.Length >= 32,
            $"JWT_CLAVE tiene {clave.Length} caracteres; HMAC-SHA256 necesita 32 o más.");

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Clave"] = clave,
            ["Jwt:Emisor"] = "ApiGateway.BFF",
            ["Jwt:Audiencia"] = "ECommerceGRPC.Clientes",
        }).Build();

        var (token, _) = new TokenService(config).Emitir(new UsuarioDemo("user@example.com", "x", "Cliente", 2));

        var resultado = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = "ApiGateway.BFF",
            ValidAudience = "ECommerceGRPC.Clientes",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(clave)),
        });
        Assert.True(resultado.IsValid, resultado.Exception?.Message);
    }
}
