$ErrorActionPreference = 'Stop'
$taskRepo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$taskRecorder = Join-Path $taskRepo 'tools\Record-DirectGameInput.ps1'
$taskName = 'Switcheroonie-private-recorder-test-' + [Guid]::NewGuid().ToString('N')
$taskLog = Join-Path ([IO.Path]::GetTempPath()) ('switcheroonie-recorder-fixture-' + [Guid]::NewGuid().ToString('N') + '.jsonl')
$taskSource = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
public sealed class SwitcheroonieRecordOnlyFixture : IDisposable {
 readonly string name; readonly CancellationTokenSource stop=new CancellationTokenSource();
 readonly List<string> requests=new List<string>(); readonly Task worker;
 public SwitcheroonieRecordOnlyFixture(string pipe) {
  name=pipe; worker=Task.Run(async ()=> {
   int number=0;
   while(!stop.IsCancellationRequested) {
    using(var server=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous)) {
     try {
      await server.WaitForConnectionAsync(stop.Token);
      string request;
      using(var reader=new StreamReader(server,Encoding.UTF8,false,1024,true)) request=await reader.ReadLineAsync();
      lock(requests) requests.Add(request);
      number++;
      string reply;
      if(number==4) reply="{\"Accepted\":true,\"Status\":null}";
      else if(number==5) reply="{\"Accepted\":true,\"Status\":\""+new string('x',33000)+"\"}";
      else {
       bool menu=number%2==0;
       reply="{\"Accepted\":true,\"Reason\":\"secret-world-and-player\",\"Status\":{"+
        "\"DriverAlive\":true,\"RoutingReady\":true,\"HasHead\":true,\"HasLeft\":true,\"HasRight\":true,"+
        "\"ServiceState\":\"Ready\",\"State\":\"Desktop\",\"Mode\":\"Desktop\",\"HeadAgeMilliseconds\":3.5,\"Epoch\":7,"+
        "\"InputOwner\":\"Game\",\"Armed\":true,\"GameInputEnabled\":true,\"GameInputActive\":true,"+
        "\"GameCursorCaptured\":"+(!menu).ToString().ToLowerInvariant()+",\"MenuNavigation\":"+menu.ToString().ToLowerInvariant()+","+
        "\"NativeMenuRisingEdges\":"+number+",\"NativeMenuPressed\":"+menu.ToString().ToLowerInvariant()+","+
        "\"NativeEffectiveActions\":"+(menu?4:0)+",\"NativeInputArmed\":true,\"NativeLastInputError\":0,"+
        "\"NativeCapabilityFlags\":63,\"NativeInputCoverage\":255,\"NativeMenuPath\":1,"+
        "\"PhysicalSamples\":100,\"RoutedSamples\":100,\"OwnerWindow\":123456,"+
        "\"GameInputDetail\":\"secret-world-and-player\",\"Detail\":\"private-file-path\",\"Display\":\"private-file-path\"}}";
      }
      byte[] data=Encoding.UTF8.GetBytes(reply+"\n"); await server.WriteAsync(data,0,data.Length,stop.Token);
     } catch(Exception e) { if(!(e is IOException || e is OperationCanceledException || e is ObjectDisposedException)) throw; }
    }
   }
  });
 }
 public string[] Requests { get { lock(requests) return requests.ToArray(); } }
 public void Dispose() { stop.Cancel(); try { worker.GetAwaiter().GetResult(); } catch(OperationCanceledException) {} stop.Dispose(); }
}
'@
Add-Type -TypeDefinition $taskSource
$taskFixture = New-Object SwitcheroonieRecordOnlyFixture($taskName)
$taskWatch = [Diagnostics.Stopwatch]::StartNew()
try {
    & $taskRecorder -Seconds 1 -IntervalMilliseconds 50 -OutputPath $taskLog -PipeName $taskName -SkipProcessInventory
    $taskLines = @(Get-Content -LiteralPath $taskLog | ForEach-Object { ConvertFrom-Json -InputObject $_ })
    $taskSamples = @($taskLines | Where-Object type -eq 'sample')
    $taskValid = @($taskSamples | Where-Object brokerResponding)
    $taskText = [IO.File]::ReadAllText($taskLog)
    $taskChecks = 0
    function Check([bool]$Condition, [string]$Label) { if (-not $Condition) { throw ('FAILED: ' + $Label) }; ++$script:taskChecks; Write-Host ('PASS ' + $Label) }
    Check ($taskLines[0].type -eq 'header' -and $taskLines[-1].type -eq 'complete' -and $taskSamples.Count -ge 6) 'Recorder produces bounded JSONL with header, snapshots and completion'
    Check ($taskWatch.Elapsed.TotalSeconds -lt 3 -and $taskLines[-1].elapsedMilliseconds -lt 2000) 'One-second collection exits within its bounded timeout budget'
    Check (@($taskFixture.Requests | ForEach-Object { (ConvertFrom-Json -InputObject $_).Name } | Where-Object { $_ -ne 'GetStatus' }).Count -eq 0) 'Every private-pipe command is GetStatus only'
    Check (@($taskFixture.Requests | ForEach-Object { (ConvertFrom-Json -InputObject $_).Id } | Select-Object -Unique).Count -eq $taskFixture.Requests.Count) 'Snapshot requests have independent IDs'
    Check (@($taskValid | Where-Object { $_.native.menuPressed -and $_.native.effectiveActions -eq 4 }).Count -gt 0 -and @($taskValid | Where-Object { -not $_.native.menuPressed -and $_.native.effectiveActions -eq 0 }).Count -gt 0) 'Native menu press and neutral levels are preserved'
    Check ($taskValid[-1].native.menuRisingEdges -gt $taskValid[0].native.menuRisingEdges -and $taskValid[0].readiness.headAgeMilliseconds -eq 3.5) 'Menu edge counters and physical pose ages survive masking'
    Check ($taskText -notmatch 'secret-world-and-player|private-file-path|123456|OwnerWindow|GameInputDetail') 'Free-form messages and window handles never enter the log'
    Check (@($taskSamples | Where-Object { $_.failure -eq 'BrokerUnavailableOrInvalidReply' }).Count -ge 2) 'Malformed and oversized replies are masked failures with continued sampling'
    Check (@($taskSamples | Where-Object { $null -ne $_.processes }).Count -eq 0) 'Private fixture does not inventory live applications'
    Write-Host ('Direct game-input recorder: {0} checks, zero failures.' -f $taskChecks)
} finally { $taskFixture.Dispose(); if (Test-Path -LiteralPath $taskLog) { [IO.File]::Delete($taskLog) } }
