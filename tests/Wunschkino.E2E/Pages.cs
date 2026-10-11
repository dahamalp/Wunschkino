namespace Wunschkino.E2E;

public static class Pages
{
    public static readonly string[] Anonymous = ["/login", "/about", "/datenschutz", "/impressum", "/not-found", "/Error", "/gibt-es-nicht"];
    public static readonly string[] SignedIn = ["/", "/wishes", "/wishes/new", "/account/password", "/account/2fa", "/about"];
    public static readonly string[] AdminOnly =
        ["/admin/users", "/admin/settings", "/admin/gaps", "/admin/blocks", "/admin/audit", "/admin/security"];

    public static IEnumerable<object[]> AnonymousData() => Anonymous.Select(p => new object[] { p });
    public static IEnumerable<object[]> SignedInData() => SignedIn.Select(p => new object[] { p });
    public static IEnumerable<object[]> AdminData() => AdminOnly.Select(p => new object[] { p });
}
