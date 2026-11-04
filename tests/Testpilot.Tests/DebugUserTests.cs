using Microsoft.EntityFrameworkCore;
using Testpilot.Core;
using Testpilot.Data;

namespace Testpilot.Tests;

public sealed class DebugUserTests
{
    [Fact]
    public void PasswordHashUsesRandomSaltAndVerifies()
    {
        var first = DebugPassword.Hash("geheim1");
        var second = DebugPassword.Hash("geheim1");

        Assert.NotEqual(first.Salt, second.Salt);
        Assert.NotEqual(first.Hash, second.Hash);
        Assert.DoesNotContain("geheim1", first.Hash, StringComparison.Ordinal);
        Assert.True(DebugPassword.Verify("geheim1", first.Hash, first.Salt, first.Iterations));
        Assert.False(DebugPassword.Verify("Geheim1", first.Hash, first.Salt, first.Iterations));
        Assert.False(DebugPassword.Verify("geheim1", "kaputt", first.Salt, first.Iterations));
        Assert.False(DebugPassword.Verify("geheim1", first.Hash, first.Salt, 0));
    }

    [Fact]
    public async Task UsersAreCreatedVerifiedChangedAndDeletedByShortName()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var db = database.NewContext())
            await new DebugUserService(db).SetPasswordAsync(" wse ", "geheim1");

        await using (var db = database.NewContext())
        {
            var service = new DebugUserService(db);
            Assert.True(await service.VerifyAsync("WSE", "geheim1"));
            Assert.True(await service.VerifyAsync("wse", "geheim1"));
            Assert.False(await service.VerifyAsync("WSE", "falsch1"));
            Assert.False(await service.VerifyAsync("ABC", "geheim1"));
            Assert.False(await service.VerifyAsync("", ""));
            await service.SetPasswordAsync("WSE", "neues-pw");
        }

        await using (var db = database.NewContext())
        {
            var service = new DebugUserService(db);
            Assert.False(await service.VerifyAsync("WSE", "geheim1"));
            Assert.True(await service.VerifyAsync("WSE", "neues-pw"));
            var user = Assert.Single(await service.ListAsync());
            Assert.Equal("WSE", user.ShortName);
            await service.DeleteAsync("wse");
        }

        await using var verify = database.NewContext();
        Assert.Empty(await verify.DebugUsers.ToListAsync());
    }

    [Theory]
    [InlineData("W", "geheim1")]
    [InlineData("W S", "geheim1")]
    [InlineData("WSE", "kurz")]
    [InlineData("ÄÖÜ", "geheim1")]
    public async Task InvalidShortNamesAndShortPasswordsAreRejected(string shortName, string password)
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.NewContext();
        await Assert.ThrowsAsync<ArgumentException>(() => new DebugUserService(db).SetPasswordAsync(shortName, password));
    }
}
