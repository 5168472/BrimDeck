using BrimDeck.Core;

// The discovery rules, checked on the documents the platforms actually send. A listing answering for its own address
// is the case that broke a correctly configured Feishu account: the collection was chosen as if it were a calendar,
// and the query against it returned nothing.
internal static class CalDavDiscoveryTests
{
    public static void Run(Action<string, bool> check)
    {
        // The shape Feishu answers with: the listing itself first, then two calendars that name no resource type.
        const string listing = """
            <?xml version="1.0" encoding="UTF-8"?>
            <D:multistatus xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
              <D:response>
                <D:href>/calendars/</D:href>
                <D:propstat><D:prop><D:resourcetype><D:collection/></D:resourcetype><D:displayname>calendars</D:displayname></D:prop></D:propstat>
              </D:response>
              <D:response>
                <D:href>/calendars/68D39CC2-1B13-4001-68D3-9CC21B134001/</D:href>
                <D:propstat><D:prop><D:resourcetype><D:collection/></D:resourcetype><D:displayname>魏树声</D:displayname></D:prop></D:propstat>
              </D:response>
              <D:response>
                <D:href>/calendars/6AE93CC3-BB18-0CCC-6AE9-3CC3BB180CCC/</D:href>
                <D:propstat><D:prop><D:resourcetype><D:collection/></D:resourcetype><D:displayname>BOT</D:displayname></D:prop></D:propstat>
              </D:response>
            </D:multistatus>
            """;
        var feishu = CalDavClient.ChooseCalendar(listing, "https://caldav.feishu.cn/calendars/");
        check("A listing is not taken for a calendar", feishu is not null && !feishu.EndsWith("/calendars/", StringComparison.OrdinalIgnoreCase));
        check("The first calendar in the listing is chosen", feishu == "https://caldav.feishu.cn/calendars/68D39CC2-1B13-4001-68D3-9CC21B134001/");

        // The standard answer: a resource type of calendar. It wins over the listing.
        const string marked = """
            <?xml version="1.0" encoding="UTF-8"?>
            <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
              <d:response>
                <d:href>/calendars/</d:href>
                <d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype><d:displayname>calendars</d:displayname></d:prop></d:propstat>
              </d:response>
              <d:response>
                <d:href>/calendars/primary/</d:href>
                <d:propstat><d:prop><d:resourcetype><d:collection/><c:calendar/></d:resourcetype><d:displayname>Calendar</d:displayname></d:prop></d:propstat>
              </d:response>
            </d:multistatus>
            """;
        check("A calendar that names its resource type is chosen",
            CalDavClient.ChooseCalendar(marked, "https://example.test/calendars/") == "https://example.test/calendars/primary/");

        // The principal is not a calendar: it names no display name and sits outside a calendars collection.
        const string principalOnly = """
            <?xml version="1.0" encoding="UTF-8"?>
            <d:multistatus xmlns:d="DAV:">
              <d:response>
                <d:href>/u_tester/</d:href>
                <d:propstat><d:prop><d:resourcetype><d:collection/><d:principal/></d:resourcetype></d:prop></d:propstat>
              </d:response>
            </d:multistatus>
            """;
        check("A principal is not a calendar", CalDavClient.ChooseCalendar(principalOnly, "https://example.test/") is null);
        // A listing with nothing else in it yields nothing rather than its own address.
        const string emptyListing = """
            <?xml version="1.0" encoding="UTF-8"?>
            <d:multistatus xmlns:d="DAV:">
              <d:response>
                <d:href>/calendars/</d:href>
                <d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop></d:propstat>
              </d:response>
            </d:multistatus>
            """;
        check("A listing with no calendars yields nothing", CalDavClient.ChooseCalendar(emptyListing, "https://example.test/calendars/") is null);

        // A path from the server is absolute to the host: it must not be resolved against the collection's own path,
        // which would put "/calendars/" in front of it a second time.
        check("An absolute href keeps its path",
            CalDavClient.AbsoluteUrl("/calendars/68D3/", "https://caldav.feishu.cn/calendars/") == "https://caldav.feishu.cn/calendars/68D3/");
        check("An href that is already a full address is used as it is",
            CalDavClient.AbsoluteUrl("https://caldav.feishu.cn/calendars/x/", "https://caldav.feishu.cn/calendars/") == "https://caldav.feishu.cn/calendars/x/");
    }
}
