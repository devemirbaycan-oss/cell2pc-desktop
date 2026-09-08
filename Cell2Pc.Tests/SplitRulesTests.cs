using Cell2Pc.Client.Net;

namespace Cell2Pc.Tests;

/// <summary>
/// Which traffic goes through the phone.
///
/// Getting this wrong is expensive in the literal sense: a rule that fails to
/// match sends a game update over a metered plan, and nothing about that is
/// visible until the bill. It also fails quietly - the connection works either
/// way, so only the byte count betrays it.
/// </summary>
public class SplitRulesTests
{
    private static byte[] Ip(string text) => System.Net.IPAddress.Parse(text).GetAddressBytes();

    [Fact]
    public void Everything_is_tunnelled_by_default()
    {
        var rules = new SplitRules();
        Assert.True(rules.ShouldTunnel(Ip("93.184.216.34")));
    }

    [Fact]
    public void An_excluded_range_bypasses_the_tunnel()
    {
        var rules = new SplitRules();
        rules.Add("203.0.113.0/24", tunnel: false);

        Assert.False(rules.ShouldTunnel(Ip("203.0.113.7")));
        Assert.True(rules.ShouldTunnel(Ip("203.0.114.7")));
    }

    [Fact]
    public void A_single_address_is_treated_as_a_host_route()
    {
        var rules = new SplitRules();
        rules.Add("198.51.100.5", tunnel: false);

        Assert.False(rules.ShouldTunnel(Ip("198.51.100.5")));
        Assert.True(rules.ShouldTunnel(Ip("198.51.100.6")));
    }

    [Fact]
    public void The_most_specific_rule_wins()
    {
        // Excluding a whole range but including one host inside it is the
        // obvious thing to want, and it only works if specificity decides.
        var rules = new SplitRules();
        rules.Add("10.0.0.0/8", tunnel: false);
        rules.Add("10.1.2.3/32", tunnel: true);

        Assert.False(rules.ShouldTunnel(Ip("10.9.9.9")));
        Assert.True(rules.ShouldTunnel(Ip("10.1.2.3")));
    }

    [Fact]
    public void Only_listed_mode_inverts_the_default()
    {
        var rules = new SplitRules();
        rules.SetMode(SplitRules.Mode.TunnelOnlyListed);
        rules.Add("203.0.113.0/24", tunnel: true);

        Assert.True(rules.ShouldTunnel(Ip("203.0.113.7")));
        Assert.False(rules.ShouldTunnel(Ip("93.184.216.34")));
    }

    [Fact]
    public void A_hostname_rule_applies_once_it_resolves()
    {
        // A hostname cannot be matched against a packet - by then only an
        // address remains - so the rule attaches when the DNS answer passes.
        var rules = new SplitRules();
        rules.Add("steamcontent.com", tunnel: false);

        // Before resolution the address is unknown, so the default applies.
        Assert.True(rules.ShouldTunnel(Ip("203.0.113.50")));

        rules.NoteResolution("cdn.steamcontent.com", new[] { Ip("203.0.113.50") });
        Assert.False(rules.ShouldTunnel(Ip("203.0.113.50")));
    }

    [Fact]
    public void A_hostname_rule_matches_subdomains_but_not_lookalikes()
    {
        var rules = new SplitRules();
        rules.Add("example.com", tunnel: false);

        rules.NoteResolution("cdn.example.com", new[] { Ip("203.0.113.1") });
        Assert.False(rules.ShouldTunnel(Ip("203.0.113.1")));

        // notexample.com must not match a rule for example.com.
        rules.NoteResolution("notexample.com", new[] { Ip("203.0.113.2") });
        Assert.True(rules.ShouldTunnel(Ip("203.0.113.2")));
    }

    [Fact]
    public void Private_ranges_are_excluded_by_default()
    {
        // Sending these through the tunnel breaks printers and NAS boxes, and
        // the phone could not reach them anyway.
        var rules = SplitRules.Load();

        Assert.False(rules.ShouldTunnel(Ip("192.168.1.50")));
        Assert.False(rules.ShouldTunnel(Ip("10.0.0.5")));
        Assert.False(rules.ShouldTunnel(Ip("172.16.4.4")));
        Assert.False(rules.ShouldTunnel(Ip("169.254.1.1")));
    }

    [Fact]
    public void Ipv6_falls_back_to_the_default_rather_than_guessing()
    {
        // v6 prefix matching is not implemented; pretending otherwise would
        // silently send v6 traffic somewhere unintended.
        var rules = new SplitRules();
        rules.Add("203.0.113.0/24", tunnel: false);

        var v6 = System.Net.IPAddress.Parse("2001:db8::1").GetAddressBytes();
        Assert.True(rules.ShouldTunnel(v6));
    }

    [Fact]
    public void A_removed_rule_stops_applying()
    {
        var rules = new SplitRules();
        rules.Add("203.0.113.0/24", tunnel: false);
        Assert.False(rules.ShouldTunnel(Ip("203.0.113.7")));

        rules.Remove("203.0.113.0/24");
        Assert.True(rules.ShouldTunnel(Ip("203.0.113.7")));
    }

    [Fact]
    public void Malformed_rules_are_ignored_rather_than_throwing()
    {
        // These come from a config file a person edits by hand.
        var rules = new SplitRules();
        rules.Add("", tunnel: false);
        rules.Add("999.999.999.999/8", tunnel: false);
        rules.Add("10.0.0.0/99", tunnel: false);

        Assert.True(rules.ShouldTunnel(Ip("93.184.216.34")));
    }
}
