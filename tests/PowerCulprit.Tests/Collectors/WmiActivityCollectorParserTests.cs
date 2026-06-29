using PowerCulprit.Collectors;

namespace PowerCulprit.Tests.Collectors;

public class WmiActivityCollectorParserTests
{
    [Fact]
    public void ParseEventXml_ExtractsClientFailureEvent()
    {
        const string xml = """
            <Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'>
              <System>
                <Provider Name='Microsoft-Windows-WMI-Activity'/>
                <EventID>5858</EventID>
                <TimeCreated SystemTime='2026-06-29T03:13:23.0709527Z'/>
                <EventRecordID>1988</EventRecordID>
              </System>
              <UserData>
                <Operation_ClientFailure xmlns='http://manifests.microsoft.com/win/2006/windows/WMI'>
                  <ClientProcessId>14724</ClientProcessId>
                  <User>machine\user</User>
                  <Operation>Start IWbemServices::ExecQuery - root\cimv2 : SELECT * FROM Win32_OperatingSystem</Operation>
                  <ResultCode>0x80041032</ResultCode>
                  <PossibleCause>Throttling Idle Tasks</PossibleCause>
                </Operation_ClientFailure>
              </UserData>
            </Event>
            """;

        var sample = WmiActivityCollector.ParseEventXml(xml);

        Assert.NotNull(sample);
        Assert.Equal(5858, sample!.EventId);
        Assert.Equal(1988, sample.EventRecordId);
        Assert.Equal(14724, sample.ClientProcessId);
        Assert.Equal("machine\\user", sample.User);
        Assert.Equal("root\\cimv2", sample.NamespaceName);
        Assert.Equal("SELECT * FROM Win32_OperatingSystem", sample.QueryText);
        Assert.Equal("0x80041032", sample.ResultCode);
        Assert.Equal("Throttling Idle Tasks", sample.PossibleCause);
    }

    [Fact]
    public void ParseEventXml_HandlesNotificationEventClientProcessID()
    {
        const string xml = """
            <Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'>
              <System>
                <EventID>5860</EventID>
                <TimeCreated SystemTime='2026-06-29T02:58:01Z'/>
              </System>
              <UserData>
                <Temporary xmlns='http://manifests.microsoft.com/win/2006/windows/WMI'>
                  <NamespaceName>root\cimv2</NamespaceName>
                  <NotificationQuery>select * from __InstanceCreationEvent within 0.1 where TargetInstance ISA 'Win32_Process'</NotificationQuery>
                  <ClientProcessID>12180</ClientProcessID>
                  <UserName>NT AUTHORITY\SYSTEM</UserName>
                  <PossibleCause>Temporary</PossibleCause>
                </Temporary>
              </UserData>
            </Event>
            """;

        var sample = WmiActivityCollector.ParseEventXml(xml);

        Assert.NotNull(sample);
        Assert.Equal(5860, sample!.EventId);
        Assert.Equal(12180, sample.ClientProcessId);
        Assert.Equal("NT AUTHORITY\\SYSTEM", sample.User);
        Assert.Contains("__InstanceCreationEvent", sample.Operation);
    }

    [Fact]
    public void ParseEventXml_MissingPid_ReturnsSampleWithInvalidPid()
    {
        const string xml = """
            <Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'>
              <System>
                <EventID>5857</EventID>
                <TimeCreated SystemTime='2026-06-29T02:58:01Z'/>
              </System>
              <UserData>
                <ProviderStarted xmlns='http://manifests.microsoft.com/win/2006/windows/WMI'>
                  <ProviderName>WMIProv</ProviderName>
                </ProviderStarted>
              </UserData>
            </Event>
            """;

        var sample = WmiActivityCollector.ParseEventXml(xml);

        Assert.NotNull(sample);
        Assert.Equal(5857, sample!.EventId);
        Assert.Equal(0, sample.ClientProcessId);
    }
}
