// Included in native/tests.cpp: portable routing fixtures, no runtime/client calls.
void TrackerSuspensionChecks()
{
    constexpr int64_t now = 20000000, frequency = 1000000;
    for (auto kind : {vr::TrackedDeviceClass_Invalid, vr::TrackedDeviceClass_HMD,
                     vr::TrackedDeviceClass_Controller, vr::TrackedDeviceClass_TrackingReference,
                     vr::TrackedDeviceClass_DisplayRedirect})
        Check(!sw::EligibleGenericTrackerClass(kind), "tracker suspension never selects a controller, HMD, reference or display class");
    Check(sw::EligibleGenericTrackerClass(vr::TrackedDeviceClass_GenericTracker),
          "public GenericTracker class is the exclusive suspension class");
    sw::Router router;
    const std::array<sw::DeviceRole, 6> roles{sw::DeviceRole::Head, sw::DeviceRole::Left,
        sw::DeviceRole::Right, sw::DeviceRole::Other, sw::DeviceRole::Other, sw::DeviceRole::Other};
    std::array<vr::DriverPose_t, 6> raw{Pose(10,1.7,2), Pose(9.8,1.1,1.8), Pose(10.2,1.1,1.8),
        Pose(10,.9,2), Pose(9.8,.1,2), Pose(8,0,2)};
    raw[3].qWorldFromDriverRotation = {std::cos(.2),0,std::sin(.2),0};
    raw[3].vecWorldFromDriverTranslation[0] = 1;
    raw[3].vecVelocity[0] = .25; raw[3].vecAngularVelocity[1] = .4;
    raw[3].poseTimeOffset = .02;
    for (uint32_t index=0; index<raw.size(); ++index)
    {
        router.SetRole(index, roles[index], 100+index);
        router.SetBodySpinEligible(index, index<5);
        router.SetGenericTracker(index, index==3 || index==4);
        router.Capture(index, raw[index], now);
    }
    sw::Request request;
    request.timestamp=now; request.epoch=10; request.requestedMode=1;
    std::array<vr::DriverPose_t,5> selected{};
    bool spin=false, suspended=false;
    for (uint32_t index=0; index<3; ++index)
        Check(router.RoutePose(index,request,true,now,frequency,selected[index],&spin,&suspended)&&
              !spin&&!suspended&&selected[index].poseIsValid&&selected[index].deviceIsConnected,
              "normal Desktop retains valid synthetic head/controllers while suspending only body trackers");
    for (uint32_t index=3; index<5; ++index)
        Check(router.RoutePose(index,request,true,now,frequency,selected[index],&spin,&suspended)&&
              !spin&&suspended&&!selected[index].poseIsValid&&!selected[index].deviceIsConnected,
              "approved GenericTracker output is explicitly invalid and disconnected in Desktop");
    vr::DriverPose_t output{};
    Check(!router.RoutePose(5,request,true,now,frequency,output),
          "unmapped Other/reference output is never mistaken for a GenericTracker");
    auto status=router.GetStatus(request,true,now,frequency);
    Check(status.actualMode&&status.genericTrackerAvailable==2&&router.TrackerAnchorsReady(request.epoch),
          "committed Desktop captures a complete immutable tracker identity set");
    auto moved=raw[3]; moved.vecPosition[0]=40;
    router.Capture(3,moved,now+1);
    sw::PoseSnapshot physical{};
    Check(router.GenericTrackerPhysical(3,physical)&&physical.container==103&&SamePose(physical.pose,moved),
          "raw moving body capture retains exact physical pose/container while its output is disconnected");
    Check(router.RoutePose(3,request,true,now+1,frequency,output,&spin,&suspended)&&suspended&&
          !output.poseIsValid&&!output.deviceIsConnected,
          "disarmed Desktop keeps tracker suspension while source continues changing");
    request.bodySpinActive=1; request.bodySpinLeaseQpc=now; request.bodySpinGeneration=1;
    request.bodySpinQuaternion[0]=std::sqrt(.5); request.bodySpinQuaternion[3]=std::sqrt(.5);
    request.bodySpinPivot[0]=10;request.bodySpinPivot[1]=.95;request.bodySpinPivot[2]=2;
    const TestVector pivot{10,.95,2};
    const vr::HmdQuaternion_t turn{std::sqrt(.5),0,0,std::sqrt(.5)};
    for (uint32_t index=0; index<5; ++index)
    {
        const auto base = index<3 ? TestWorldPosition(selected[index]) : TestWorldPosition(raw[index]);
        const auto rotated=MatrixRotate(turn,{base.x-pivot.x,base.y-pivot.y,base.z-pivot.z});
        Check(router.RoutePose(index,request,true,now+1,frequency,output,&spin,&suspended)&&spin&&!suspended&&
              output.poseIsValid&&output.deviceIsConnected&&SameVector(TestWorldPosition(output),
                  {pivot.x+rotated.x,pivot.y+rotated.y,pivot.z+rotated.z}),
              "Desktop head/controllers/anchored body trackers share one independent rigid matrix oracle");
    }
    vr::DriverPose_t stable{};
    Check(router.RoutePose(3,request,true,now+1,frequency,stable)&&stable.vecVelocity[0]==0&&
          stable.vecAngularVelocity[1]==0&&stable.poseTimeOffset==0,
          "frozen spun tracker prediction derivatives cannot retain physical motion");
    for (int sample=0; sample<80; ++sample)
    {
        moved.vecPosition[0]=50+sample;
        router.Capture(3,moved,now+sample+2);
        Check(router.RoutePose(3,request,true,now+sample+2,frequency,output)&&SamePose(output,stable),
              "same Desktop epoch spin holds the original calibrated tracker anchor despite later physical body motion");
    }
    auto invalid=raw[4];invalid.poseIsValid=false;
    router.Capture(4,invalid,now+90);
    Check(!router.BodySpinPermitted(request,true,now+90,frequency)&&
          router.SpinBlockReason(request,true,now+90,frequency)==sw::BodySpinBlockReason::StalePhysicalRig,
          "invalid physical member refuses the whole Desktop rig spin with an explicit reason");
    for (uint32_t index=0; index<5; ++index)
        Check(router.RoutePose(index,request,true,now+90,frequency,output,&spin,&suspended)&&!spin&&
              (index<3 ? output.deviceIsConnected&&!suspended : suspended&&!output.deviceIsConnected),
              "blocked spin leaves head/hands unspun and returns every body tracker to suspension");
    router.Capture(4,raw[4],now+91);
    Check(!router.BodySpinPermitted(request,true,now+91,frequency)&&
          router.GetStatus(request,true,now+91,frequency).bodySpinBlockReason==1,
          "short invalid tracker interval remains latched after recovery before broker observation");
    ++request.bodySpinGeneration;
    Check(router.RoutePose(3,request,true,now+91,frequency,output)&&SamePose(output,stable),
          "explicit new spin generation can reuse an unchanged immutable Desktop rig without recapturing physical motion");
    router.SetBodySpinEligible(5,true);router.SetGenericTracker(5,true);router.Capture(5,raw[5],now+92);
    Check(!router.BodySpinPermitted(request,true,now+92,frequency)&&
          router.SpinBlockReason(request,true,now+92,frequency)==sw::BodySpinBlockReason::DesktopRigIdentityChanged,
          "new GenericTracker cannot enter an old immutable Desktop rig spin");
    for (uint32_t index=0; index<6; ++index)
        Check(router.RoutePose(index,request,true,now+92,frequency,output,&spin,&suspended)&&!spin&&
              (index<3 ? !suspended : suspended&&!output.deviceIsConnected),
              "new body identity suspends all body trackers instead of partially rotating the old rig");
    ++request.epoch;++request.bodySpinGeneration;request.timestamp=request.bodySpinLeaseQpc=now+93;
    Check(router.Desktop(request,true,now+93,frequency)&&router.TrackerAnchorsReady(request.epoch)&&
          router.BodySpinPermitted(request,true,now+93,frequency),
          "a new Desktop transaction captures the expanded rig once");
    router.SetRole(4,sw::DeviceRole::Other,204);router.SetBodySpinEligible(4,true);router.SetGenericTracker(4,true);
    Check(!router.GenericTrackerPhysical(4,physical)&&
          !router.OriginalForRestore(4,now+94,frequency,output)&&
          !router.BodySpinPermitted(request,true,now+94,frequency),
          "same-index container replacement cannot reuse old physical pose or old body anchor");
    router.Capture(4,raw[4],now+95);
    Check(router.RoutePose(4,request,true,now+95,frequency,output,&spin,&suspended)&&suspended&&!spin&&
          !output.deviceIsConnected&&router.SpinBlockReason(request,true,now+95,frequency)==
              sw::BodySpinBlockReason::DesktopRigIdentityChanged,
          "fresh replacement can be suspended but cannot silently join the old spin set");
    router.SetRole(0,sw::DeviceRole::Head,200);router.SetBodySpinEligible(0,true);
    Check(!router.BodySpinPermitted(request,true,now+96,frequency)&&
          !router.Desktop(request,true,now+96,frequency),
          "reapproved replacement HMD with no new capture never inherits old head freshness");
    Check(!router.GetStatus(request,true,now+96,frequency).hasHead,
          "replacement HMD status cannot expose a previous container's fresh source");
    router.Capture(0,raw[0],now+97);
    Check(!router.Desktop(request,true,now+97,frequency),
          "new HMD capture cannot reuse the previous device's same-epoch Desktop anchor");
    ++request.epoch;++request.bodySpinGeneration;request.timestamp=request.bodySpinLeaseQpc=now+98;
    Check(router.Desktop(request,true,now+98,frequency)&&router.BodySpinPermitted(request,true,now+98,frequency),
          "fresh Desktop transaction after replacement captures only the current complete rig");
    router.SetRole(2,sw::DeviceRole::Right,202);router.SetBodySpinEligible(2,true);
    Check(!router.RoutePose(2,request,true,now+98,frequency,output)&&
          !router.GetStatus(request,true,now+98,frequency).hasRight,
          "reapproved controller replacement waits for its own captured provider context");
    router.Capture(2,raw[2],now+98);
    Check(router.RoutePose(2,request,true,now+98,frequency,output),
          "fresh current-controller capture enables replacement routing safely");
    request.bodySpinActive=0;
    Check(router.RoutePose(3,request,true,now+99,frequency,output,&spin,&suspended)&&suspended&&!spin,
          "spin release during Desktop resumes tracker suspension rather than physical FBT");
    request.requestedMode=0;++request.epoch;request.timestamp=now+100;
    Check(!router.RoutePose(3,request,true,now+100,frequency,output)&&
          router.OriginalForRestore(3,now+100,frequency,output)&&SamePose(output,moved),
          "Physical return restores the newest actual tracker pose instead of its anchor");
    Check(router.OriginalForRestore(3,now+frequency,frequency,output)&&!output.poseIsValid,
          "stale restoration remains invalid and cannot be advertised as fresh physical tracking");
    sw::Status extension;extension.genericTrackerAvailable=10;extension.genericTrackerSuspended=10;
    extension.bodySpinBlockReason=2;extension.bodySpinAttemptGeneration=12345;alignas(8) std::byte memory[512]{};
    sw::WriteBlock(memory,extension);sw::Status roundtrip{};
    Check(sw::ReadBlock(memory,roundtrip)&&roundtrip.genericTrackerAvailable==10&&
          roundtrip.genericTrackerSuspended==10&&roundtrip.bodySpinBlockReason==2&&
          roundtrip.bodySpinAttemptGeneration==12345&&roundtrip.version==1,
          "additive tracker telemetry roundtrips in the unchanged version1 512-byte status block");
}

void CompleteRigSpinAdmissionChecks()
{
    constexpr int64_t now=40000000, frequency=1000000;
    for (auto mode : {0u,1u})
        for (auto failed : {1u,3u})
            for (auto invalidSource : {false,true})
            {
                sw::Router router;
                std::array<vr::DriverPose_t,4> raw{Pose(10),Pose(9.8),Pose(10.2),Pose(10,.9)};
                for (uint32_t index=0;index<4;++index)
                {
                    router.SetRole(index,index==0?sw::DeviceRole::Head:index==1?sw::DeviceRole::Left:
                                         index==2?sw::DeviceRole::Right:sw::DeviceRole::Other,100+index);
                    router.SetBodySpinEligible(index,true);router.SetGenericTracker(index,index==3);
                    router.Capture(index,raw[index],now);
                }
                sw::Request request;request.epoch=1;request.requestedMode=mode;
                request.timestamp=request.bodySpinLeaseQpc=now;
                request.bodySpinActive=1;request.bodySpinGeneration=100;
                request.bodySpinQuaternion[0]=request.bodySpinQuaternion[3]=std::sqrt(.5);
                vr::DriverPose_t out{};bool spin=false,suspended=false;
                Check(router.RoutePose(1,request,true,now,frequency,out,&spin)&&spin,
                      "fresh complete HMD/controller/GenericTracker rig admits spin in either mode");
                const auto checkTime=invalidSource?now+1:now+201000;
                request.timestamp=request.bodySpinLeaseQpc=checkTime;
                for (uint32_t index=0;index<4;++index)
                    if(index!=failed) router.Capture(index,raw[index],checkTime);
                if(invalidSource)
                {
                    auto bad=raw[failed];bad.poseIsValid=false;
                    router.Capture(failed,bad,checkTime);
                    // Recovery happens before any permission/status polling.
                    router.Capture(failed,raw[failed],checkTime);
                }
                Check(!router.BodySpinPermitted(request,true,checkTime,frequency)&&
                      router.SpinBlockReason(request,true,checkTime,frequency)==sw::BodySpinBlockReason::StalePhysicalRig,
                      "a stale or briefly invalid hand/body member blocks the entire rig, including unobserved invalid recovery");
                for(uint32_t index=0;index<4;++index)
                {
                    router.RoutePose(index,request,true,checkTime,frequency,out,&spin,&suspended);
                    Check(!spin,"whole-rig refusal never leaves another head/hand/tracker member rotating");
                }
                router.Capture(failed,raw[failed],checkTime);
                Check(!router.BodySpinPermitted(request,true,checkTime,frequency),
                      "fresh complete recovery cannot revive a retired activation token");
                auto inactive=request;inactive.bodySpinActive=0;
                Check(!router.BodySpinPermitted(inactive,true,checkTime,frequency),
                      "inactive retained generation retires rather than erases its refused tombstone");
                ++request.bodySpinGeneration;
                Check(router.RoutePose(1,request,true,checkTime,frequency,out,&spin)&&spin,
                      "a greater explicit activation token admits the restored complete rig");
                Check(!router.BodySpinPermitted(inactive,true,checkTime,frequency)&&
                      router.BodySpinPermitted(request,true,checkTime,frequency),
                      "delayed inactive older packet cannot cancel the newer admitted generation");
                auto older=request;--older.bodySpinGeneration;
                Check(!router.BodySpinPermitted(older,true,checkTime,frequency),
                      "delayed older active callback cannot reactivate a retired generation");
                older.bodySpinLeaseQpc=0;
                Check(!router.BodySpinPermitted(older,true,checkTime,frequency)&&
                      router.BodySpinPermitted(request,true,checkTime,frequency),
                      "older malformed lease cannot retire the newer admitted generation");
                router.SetBodySpinEligible(failed,false);router.SetBodySpinEligible(failed,true);
                Check(!router.BodySpinPermitted(request,true,checkTime,frequency),
                      "brief eligibility loss cannot disappear between broker polls");
            }
    sw::Router concurrent;
    for(uint32_t i=0;i<4;++i)
    {
        concurrent.SetRole(i,i==0?sw::DeviceRole::Head:i==1?sw::DeviceRole::Left:
                           i==2?sw::DeviceRole::Right:sw::DeviceRole::Other,100+i);
        concurrent.SetBodySpinEligible(i,true);concurrent.SetGenericTracker(i,i==3);
        concurrent.Capture(i,Pose(i),now);
    }
    sw::Request active;active.timestamp=active.bodySpinLeaseQpc=now;active.epoch=1;
    active.bodySpinActive=1;active.bodySpinGeneration=100;
    active.bodySpinQuaternion[0]=1;
    vr::DriverPose_t output{};
    Check(concurrent.RoutePose(1,active,true,now,frequency,output),
          "concurrent refusal fixture first proves its complete rig activation is admitted");
    auto cancelled=active;cancelled.bodySpinActive=0;
    std::atomic<bool> begin{};std::vector<std::thread> threads;
    for(int worker=0;worker<8;++worker)
        threads.emplace_back([&,worker]{
            while(!begin.load(std::memory_order_acquire)) std::this_thread::yield();
            for(int sample=0;sample<1000;++sample)
            {
                if(worker%2) concurrent.BodySpinPermitted(cancelled,true,now,frequency);
                else
                {
                    auto invalid=Pose(3);invalid.poseIsValid=false;
                    concurrent.Capture(3,invalid,now);concurrent.Capture(3,Pose(3),now);
                }
            }
        });
    begin.store(true,std::memory_order_release);
    for(auto &thread:threads) thread.join();
    concurrent.Capture(3,Pose(3),now);
    Check(!concurrent.BodySpinPermitted(active,true,now,frequency)&&
          concurrent.SpinBlockReason(active,true,now,frequency)==sw::BodySpinBlockReason::StalePhysicalRig,
          "concurrent cancellation and recovered invalid capture cannot lose the refusal tombstone");
    ++active.bodySpinGeneration;
    Check(concurrent.RoutePose(1,active,true,now,frequency,output),
          "a fresh explicit token after concurrent refusal restores safe complete-rig admission");

    sw::PoseStore store;
    struct PublishedCache { std::atomic<bool> entered{}, release{}, valid{}; } cache;
    bool firstWritten=false;
    std::thread first([&]{firstWritten=store.Write({Pose(1),now,0,101},[](void *context) noexcept {
        auto &state=*static_cast<PublishedCache *>(context);
        state.entered.store(true,std::memory_order_release);
        while(!state.release.load(std::memory_order_acquire)) std::this_thread::yield();
        state.valid.store(true,std::memory_order_release);
    },&cache);});
    while(!cache.entered.load(std::memory_order_acquire)) std::this_thread::yield();
    auto bad=Pose(2);bad.poseIsValid=false;
    Check(!store.Write({bad,now+1,0,101},[](void *context) noexcept {
        static_cast<PublishedCache *>(context)->valid.store(false,std::memory_order_release);
    },&cache),"a second pose callback cannot commit its cache while the first publisher still owns the writer");
    sw::PoseSnapshot captured{};
    Check(!store.Read(captured),"the pose seqlock remains unpublished until its capture cache publisher completes");
    cache.release.store(true,std::memory_order_release);first.join();
    Check(firstWritten&&store.Read(captured)&&captured.pose.poseIsValid&&cache.valid.load(),
          "completed original pose and cache describe the same first writer");
    Check(store.Write({bad,now+1,0,101},[](void *context) noexcept {
        static_cast<PublishedCache *>(context)->valid.store(false,std::memory_order_release);
    },&cache)&&store.Read(captured)&&!captured.pose.poseIsValid&&!cache.valid.load(),
          "a later invalid committed pose cannot be overwritten by the earlier writer's delayed cache publication");
}
