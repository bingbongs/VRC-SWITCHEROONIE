// Included after the independent Host/Input classes: real MinHook, no runtime.
void TrackerSuspensionHookChecks()
{
    alignas(8) std::byte memory[512]{};
    sw::Request request;request.timestamp=sw::QpcNow();sw::WriteBlock(memory,request);
    HostState state;
    Host own(state,0), head(state,1), left(state,2), right(state,4), body(state,8), other(state,16);
    InputState inputs;Input ownInput(inputs,0);
    sw::Router router;
    for (uint32_t index=0;index<5;++index)
    {
        router.SetRole(index,index==0?sw::DeviceRole::Head:index==1?sw::DeviceRole::Left:
                             index==2?sw::DeviceRole::Right:sw::DeviceRole::Other,100+index);
        router.SetBodySpinEligible(index,index<4);
        router.SetGenericTracker(index,index==3);
    }
    sw::HookRuntime hooks(router,memory);
    Check(hooks.Install(&own,&ownInput,false),"tracker suspension fixture installs isolated real method hooks");
    CallPose(&head,0,Pose(0));CallPose(&left,1,Pose(-.2));CallPose(&right,2,Pose(.2));
    CallPose(&body,3,Pose(7));CallPose(&other,4,Pose(8));
    request.requestedMode=1;request.epoch=1;request.timestamp=sw::QpcNow();sw::WriteBlock(memory,request);
    auto otherCount=state.accepted[4];hooks.Tick(request.timestamp);
    Check(!state.output[3].poseIsValid&&!state.output[3].deviceIsConnected&&
          state.output[0].deviceIsConnected&&state.output[1].deviceIsConnected&&state.output[2].deviceIsConnected&&
          state.accepted[4]==otherCount&&!state.wrongOwner,
          "frame-only Desktop suspends GenericTracker through its actual vendor context and leaves Other/head/hands intact");
    auto status=hooks.StatusNow(sw::QpcNow());
    Check(status.genericTrackerAvailable==1&&status.genericTrackerSuspended==1&&
          (status.reserved0&sw::GenericTrackerSuspensionCapability),
          "actual disconnected-output ownership is reported separately from approved tracker inventory");
    CallPose(&body,3,Pose(9));sw::PoseSnapshot raw{};
    Check(!state.output[3].deviceIsConnected&&router.GenericTrackerPhysical(3,raw)&&
          raw.pose.deviceIsConnected&&raw.pose.poseIsValid&&raw.pose.vecPosition[0]==9,
          "vendor moving tracker stream remains independent of callback suspension");
    auto suspendedCount=state.accepted[3];
    request.requestedMode=0;++request.epoch;request.timestamp=sw::QpcNow();sw::WriteBlock(memory,request);
    hooks.Tick(request.timestamp);
    Check(state.accepted[3]==suspendedCount+1&&state.output[3].deviceIsConnected&&
          state.output[3].poseIsValid&&state.output[3].vecPosition[0]==9&&
          !hooks.StatusNow(sw::QpcNow()).genericTrackerSuspended&&!state.wrongOwner,
          "Physical return restores newest independent tracker even with no new vendor callback");
    request.requestedMode=1;++request.epoch;request.timestamp=sw::QpcNow();sw::WriteBlock(memory,request);
    hooks.Tick(request.timestamp);
    request.bodySpinActive=1;request.bodySpinGeneration=1;
    request.bodySpinQuaternion[0]=std::sqrt(.5);request.bodySpinQuaternion[3]=std::sqrt(.5);
    request.bodySpinPivot[1]=.95;request.timestamp=request.bodySpinLeaseQpc=sw::QpcNow();sw::WriteBlock(memory,request);
    hooks.Tick(request.timestamp);auto firstSpun=state.output[3];
    CallPose(&body,3,Pose(19));hooks.Tick(sw::QpcNow());
    auto a=WorldPoint(firstSpun),b=WorldPoint(state.output[3]);
    Check(state.output[3].deviceIsConnected&&state.output[3].poseIsValid&&
          std::abs(a[0]-b[0])<1e-9&&std::abs(a[1]-b[1])<1e-9&&std::abs(a[2]-b[2])<1e-9&&
          router.GenericTrackerPhysical(3,raw)&&raw.pose.vecPosition[0]==19,
          "Desktop real-hook spin uses frozen calibrated tracker anchor rather than newly moving physical body");
    status=hooks.StatusNow(sw::QpcNow());
    Check(status.bodySpinActive&&status.bodySpinSamples>=5&&status.genericTrackerSuspended==0&&
          !status.bodySpinBlockReason,"complete anchored Desktop rig can actually submit spin instead of suspension");
    router.SetBodySpinEligible(4,true);router.SetGenericTracker(4,true);
    CallPose(&other,4,Pose(8));hooks.Tick(sw::QpcNow());
    status=hooks.StatusNow(sw::QpcNow());
    Check(!status.bodySpinActive&&status.bodySpinBlockReason==2&&
          !state.output[3].deviceIsConnected&&!state.output[4].deviceIsConnected&&
          state.output[0].qWorldFromDriverRotation.w==1&&state.output[1].qWorldFromDriverRotation.w==1&&
          status.genericTrackerAvailable==2&&status.genericTrackerSuspended==2,
          "new body identity blocks whole-rig spin and immediately restores unspun head/hands plus disconnected trackers");
    request.bodySpinActive=0;request.timestamp=sw::QpcNow();sw::WriteBlock(memory,request);hooks.Tick(request.timestamp);
    Check(!state.output[3].deviceIsConnected&&!state.output[4].deviceIsConnected,
          "Desktop spin release retains the ordinary fallback suspension policy");
    request.timestamp-=sw::QpcFrequency()/4;sw::WriteBlock(memory,request);hooks.Tick(sw::QpcNow());
    Check(state.output[3].deviceIsConnected&&state.output[3].poseIsValid&&state.output[3].vecPosition[0]==19&&
          state.output[4].deviceIsConnected&&state.output[4].poseIsValid&&
          !hooks.StatusNow(sw::QpcNow()).genericTrackerSuspended,
          "expired broker lease restores every owned tracker without requiring fresh provider callbacks");
    request.timestamp=sw::QpcNow();++request.epoch;sw::WriteBlock(memory,request);hooks.Tick(request.timestamp);
    Sleep(210);request.requestedMode=0;++request.epoch;request.timestamp=sw::QpcNow();sw::WriteBlock(memory,request);
    hooks.Tick(request.timestamp);
    Check(!state.output[3].poseIsValid&&!state.output[4].poseIsValid&&
          state.output[3].result==vr::TrackingResult_Uninitialized,
          "stale tracker restoration cannot become fabricated fresh physical tracking");
    CallPose(&head,0,Pose());CallPose(&body,3,Pose(21));CallPose(&other,4,Pose(22));
    request.requestedMode=1;++request.epoch;request.timestamp=sw::QpcNow();sw::WriteBlock(memory,request);hooks.Tick(request.timestamp);
    router.SetRole(3,sw::DeviceRole::Other,203);router.SetBodySpinEligible(3,true);router.SetGenericTracker(3,true);
    request.requestedMode=0;++request.epoch;request.timestamp=sw::QpcNow();sw::WriteBlock(memory,request);
    auto oldContainerCalls=state.accepted[3];hooks.Tick(request.timestamp);
    Check(state.accepted[3]==oldContainerCalls,
          "periodic restore never writes a prior tracker capture into a replacement container");
    CallPose(&body,3,Pose(31));
    Check(state.output[3].poseIsValid&&state.output[3].deviceIsConnected&&state.output[3].vecPosition[0]==31,
          "replacement's own vendor callback resumes Physical normally");
    request.requestedMode=1;++request.epoch;request.timestamp=sw::QpcNow();sw::WriteBlock(memory,request);hooks.Tick(request.timestamp);
    auto beforeRemove=state.accepted[3];
    Check(hooks.Remove()&&state.accepted[3]==beforeRemove+1&&state.output[3].deviceIsConnected&&
          state.output[3].vecPosition[0]==31&&!state.wrongOwner,
          "driver cleanup restores suspended trackers through captured correct provider contexts");
}
