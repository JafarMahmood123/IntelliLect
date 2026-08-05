using EmailService.Infrastructure.Services;

namespace EmailService.UnitTests;

/// <summary>
/// The email templates. Pure string building — no SMTP, no broker.
///
/// Two things are worth protecting here. The first is that every branch says something different:
/// a status mapping that silently returns the same wording for "rejected" as for "active" is the
/// kind of bug nobody notices until a rejected applicant is congratulated. The second is that
/// values which came from a user stay inert — see the injection tests.
/// </summary>
public sealed class EmailBodyFactoryTests
{
    private readonly EmailBodyFactory _factory = new();

    // --- codes ------------------------------------------------------------------------

    [Fact]
    public void Password_reset_body_carries_the_code_and_says_how_long_it_lasts()
    {
        var body = _factory.CreatePasswordResetBody("482913");

        Assert.Contains("482913", body);
        // The expiry is the difference between a usable code and a support ticket.
        Assert.Contains("15 minutes", body);
    }

    [Fact]
    public void Two_factor_body_carries_the_code_and_its_own_shorter_expiry()
    {
        var body = _factory.CreateTwoFactorCodeBody("104857");

        Assert.Contains("104857", body);
        Assert.Contains("5 minutes", body);
        // Not the reset copy with a different number — the two emails arrive in the same inbox
        // and must not be mistakable for each other.
        Assert.Contains("sign-in attempt", body, StringComparison.OrdinalIgnoreCase);
    }

    // --- status changes ---------------------------------------------------------------

    [Theory]
    [InlineData("pending", "We have received your registration request")]
    [InlineData("active", "Your account has been approved")]
    [InlineData("rejected", "we are unable to approve your account")]
    [InlineData("deactivated", "Your account has been deactivated")]
    public void Each_status_gets_its_own_wording(string status, string expected)
    {
        var body = _factory.CreateStatusChangedBody("Amina", status);

        Assert.Contains(expected, body);
        Assert.Contains("Hello Amina", body);
    }

    [Fact]
    public void Status_matching_ignores_case()
    {
        // The producer sends an enum name — "Active", not "active". Matching only lowercase would
        // send every approved user the generic fallback.
        Assert.Contains("Your account has been approved", _factory.CreateStatusChangedBody("Amina", "Active"));
        Assert.Contains("Your account has been approved", _factory.CreateStatusChangedBody("Amina", "ACTIVE"));
    }

    [Fact]
    public void An_unknown_status_falls_back_to_neutral_wording_rather_than_failing()
    {
        // A status this service has not been taught yet must still produce a sendable email. The
        // wording says nothing it cannot know — in particular it does not congratulate anyone.
        var body = _factory.CreateStatusChangedBody("Amina", "Suspended");

        Assert.Contains("There has been an update to your account status", body);
        Assert.DoesNotContain("approved", body, StringComparison.OrdinalIgnoreCase);
    }

    // --- classroom changes ------------------------------------------------------------

    [Fact]
    public void Teacher_assignment_and_reassignment_read_differently()
    {
        var assigned = _factory.CreateTeacherChangedBody("Amina", "Optics", isNewTeacher: true);
        var removed = _factory.CreateTeacherChangedBody("Amina", "Optics", isNewTeacher: false);

        Assert.Contains("assigned as the teacher", assigned);
        Assert.Contains("no longer under your management", removed);
        // The reassured half matters: a teacher losing a classroom needs to know nothing was lost.
        Assert.Contains("unchanged", removed);
        Assert.Contains("Optics", assigned);
        Assert.Contains("Optics", removed);
    }

    [Fact]
    public void Membership_added_and_removed_read_differently()
    {
        var added = _factory.CreateMembershipChangedBody("Bilal", "Optics", isAdded: true);
        var removed = _factory.CreateMembershipChangedBody("Bilal", "Optics", isAdded: false);

        Assert.Contains("have been added to the classroom", added);
        Assert.Contains("have been removed from the classroom", removed);
        Assert.Contains("Bilal", added);
    }

    // --- injection --------------------------------------------------------------------

    [Fact]
    public void A_name_containing_markup_is_encoded_rather_than_rendered()
    {
        // A first name is whatever someone typed into the registration form. Interpolated raw, it
        // lands as live markup inside an email carrying our name and branding — and a mail client
        // that refuses to run script will still render a link.
        var body = _factory.CreateStatusChangedBody("<a href='http://evil'>click</a>", "active");

        Assert.DoesNotContain("<a href", body);
        Assert.Contains("&lt;a href", body);
    }

    [Fact]
    public void A_classroom_name_containing_markup_is_encoded_in_both_templates()
    {
        const string hostile = "<img src=x onerror=alert(1)>";

        var teacher = _factory.CreateTeacherChangedBody("Amina", hostile, isNewTeacher: true);
        var membership = _factory.CreateMembershipChangedBody("Amina", hostile, isAdded: true);

        Assert.DoesNotContain("<img", teacher);
        Assert.DoesNotContain("<img", membership);
        Assert.Contains("&lt;img", teacher);
        Assert.Contains("&lt;img", membership);
    }

    [Fact]
    public void Encoding_a_name_does_not_break_the_surrounding_template()
    {
        // The encoding must not escape the template's own markup along with the value.
        var body = _factory.CreateStatusChangedBody("<b>", "active");

        Assert.Contains("<div style=", body);
        Assert.Contains("<h1 style='color: #08060d;'>IntelliLect</h1>", body);
    }

    [Fact]
    public void A_null_name_produces_an_email_rather_than_an_exception()
    {
        // The message contract does not stop a producer sending null, and a crashed consumer
        // costs the email AND the queue slot.
        var body = _factory.CreateStatusChangedBody(null!, "active");

        Assert.Contains("Your account has been approved", body);
    }
}
