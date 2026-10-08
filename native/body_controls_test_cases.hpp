// Included inside the isolated routing test namespace; no runtime/client APIs.
struct TestVector { double x, y, z; };
TestVector MatrixRotate(vr::HmdQuaternion_t q, TestVector v)
{
    const auto n = q.w*q.w+q.x*q.x+q.y*q.y+q.z*q.z;
    const auto m00 = 1-2*(q.y*q.y+q.z*q.z)/n;
    const auto m01 = 2*(q.x*q.y-q.w*q.z)/n;
    const auto m02 = 2*(q.x*q.z+q.w*q.y)/n;
    const auto m10 = 2*(q.x*q.y+q.w*q.z)/n;
    const auto m11 = 1-2*(q.x*q.x+q.z*q.z)/n;
    const auto m12 = 2*(q.y*q.z-q.w*q.x)/n;
    const auto m20 = 2*(q.x*q.z-q.w*q.y)/n;
    const auto m21 = 2*(q.y*q.z+q.w*q.x)/n;
    const auto m22 = 1-2*(q.x*q.x+q.y*q.y)/n;
    return {m00*v.x+m01*v.y+m02*v.z,m10*v.x+m11*v.y+m12*v.z,
            m20*v.x+m21*v.y+m22*v.z};
}
TestVector TestWorldPosition(const vr::DriverPose_t &p)
{
    const auto head = MatrixRotate(p.qRotation,{p.vecDriverFromHeadTranslation[0],
        p.vecDriverFromHeadTranslation[1],p.vecDriverFromHeadTranslation[2]});
    const auto position = MatrixRotate(p.qWorldFromDriverRotation,
        {p.vecPosition[0]+head.x,p.vecPosition[1]+head.y,p.vecPosition[2]+head.z});
    return {position.x+p.vecWorldFromDriverTranslation[0],
            position.y+p.vecWorldFromDriverTranslation[1],
            position.z+p.vecWorldFromDriverTranslation[2]};
}
vr::HmdQuaternion_t TestWorldRotation(const vr::DriverPose_t &p)
{
    return Multiply(Multiply(p.qWorldFromDriverRotation,p.qRotation),p.qDriverFromHeadRotation);
}
bool SameVector(TestVector a, TestVector b)
{
    return Near(a.x,b.x)&&Near(a.y,b.y)&&Near(a.z,b.z);
}
double DistanceSquared(TestVector a, TestVector b)
{
    return (a.x-b.x)*(a.x-b.x)+(a.y-b.y)*(a.y-b.y)+(a.z-b.z)*(a.z-b.z);
}
void BodyControlsChecks()
{
    constexpr int64_t now = 10000000, frequency = 1000000;
    constexpr double pi = 3.14159265358979323846;
    for (auto kind : {vr::TrackedDeviceClass_HMD,vr::TrackedDeviceClass_Controller,
                     vr::TrackedDeviceClass_GenericTracker})
        Check(sw::EligibleBodyTrackedClass(kind),"only body-tracked classes enter the spin eligibility allowlist");
    for (auto kind : {vr::TrackedDeviceClass_Invalid,vr::TrackedDeviceClass_TrackingReference,
                     vr::TrackedDeviceClass_DisplayRedirect,vr::TrackedDeviceClass_Max})
        Check(!sw::EligibleBodyTrackedClass(kind),"tracking references and display-only devices never enter the body-spin transform");
    sw::Router router;
    router.SetRole(0,sw::DeviceRole::Head,100);
    router.SetRole(1,sw::DeviceRole::Left,101);
    router.SetRole(2,sw::DeviceRole::Right,102);
    router.SetRole(3,sw::DeviceRole::Other,103);
    router.SetRole(4,sw::DeviceRole::Other,104);
    std::array<vr::DriverPose_t,5> original{
        Pose(10,1.7,2),Pose(9.8,1.1,1.8),Pose(10.2,1.1,1.8),Pose(10,.9,2),Pose(8,0,2)};
    original[2].qWorldFromDriverRotation = {std::cos(.175),0,std::sin(.175),0};
    original[2].vecWorldFromDriverTranslation[0]=3;
    original[2].vecWorldFromDriverTranslation[1]=.2;
    original[2].vecWorldFromDriverTranslation[2]=-1;
    original[2].qRotation = Multiply({std::cos(.1),0,std::sin(.1),0},
                                    {std::cos(.15),std::sin(.15),0,0});
    original[2].qDriverFromHeadRotation={std::cos(.05),0,-std::sin(.05),0};
    for (int i=0;i<3;++i)
    {
        original[2].vecDriverFromHeadTranslation[i]=.01*(i+1);
        original[2].vecVelocity[i]=.1*(i+1);
        original[2].vecAngularVelocity[i]=-.1*(i+1);
    }
    for (uint32_t i=0;i<original.size();++i)
    {
        router.Capture(i,original[i],now);
        router.SetBodySpinEligible(i,i<4);
    }
    sw::Request request;
    request.timestamp=now;request.epoch=1;request.requestedMode=request.armed=1;request.height=.15;
    std::array<vr::DriverPose_t,4> baseline{};
    for (uint32_t i=0;i<3;++i)
        Check(router.RoutePose(i,request,true,now,frequency,baseline[i]),"raw neutral desktop pose routes before menu navigation");
    Check(SameRotation(baseline[1].qRotation,{1,0,0,0})&&
          SameRotation(baseline[2].qRotation,{1,0,0,0}),"both resting wrists are straight raw poses without a tip assumption");
    request.handPreset=uint32_t(sw::HandPreset::LeftMenuNavigation);
    vr::DriverPose_t menuLeft{},pointerRight{},after{};
    Check(router.RoutePose(1,request,true,now,frequency,menuLeft)&&
          router.RoutePose(2,request,true,now,frequency,pointerRight)&&
          menuLeft.vecPosition[2]<baseline[1].vecPosition[2]-.2&&
          menuLeft.vecPosition[1]>baseline[1].vecPosition[1]+.4&&
          pointerRight.vecPosition[2]<baseline[2].vecPosition[2]-.4,
          "persistent menu navigation raises the left menu wrist while keeping the right pointer available");
    request.pitch=1.2;request.handYaw=.7;request.handPitch=.6;
    Check(router.RoutePose(1,request,true,now,frequency,after)&&SamePose(after,menuLeft),
          "head pitch and right fine aim never bend the persistent left menu wrist");
    Check(router.RoutePose(2,request,true,now,frequency,after)&&
          SameRotation(after.qRotation,Multiply({std::cos(-.35),0,std::sin(-.35),0},
                                                {std::cos(.3),std::sin(.3),0,0})),
          "right menu pointer uses the raw requested yaw/pitch without the disproven tip correction");
    for (auto control : {sw::Control::Menu,sw::Control::Trigger,sw::Control::Grip,sw::Control::Touch})
        for (auto role : {sw::DeviceRole::Left,sw::DeviceRole::Right})
            Check(sw::DesiredValue(control,role,request)==0,"menu hand posture itself does not generate menu clicks or finger gestures");
    router.Capture(0,Pose(20,1.8,3),now+1);
    Check(router.RoutePose(1,request,true,now+1,frequency,after)&&SamePose(after,menuLeft),
          "same desktop epoch keeps persistent menu navigation stable during real head motion");
    request.armed=0;
    Check(router.RoutePose(1,request,true,now+1,frequency,after)&&SamePose(after,baseline[1]),
          "focus disarm overrides the persistent menu preset and returns the left wrist to rest");
    request.armed=1;request.handPreset=0;request.pitch=request.handYaw=request.handPitch=0;
    router.Capture(0,original[0],now);
    sw::Error error{};
    request.handPreset=4;
    Check(!sw::Router::ValidRequest(request,now,frequency,error),"undefined menu posture values fail closed");
    request.handPreset=0;
    Check(sw::Router::ValidRequest(request,now,frequency,error)&&
          !sw::Router::BodySpinActive(request,true,now,frequency),"old brokers with the zero extension keep normal unspun behavior");
    request.bodySpinActive=1;request.bodySpinLeaseQpc=now;request.bodySpinGeneration=1;
    request.bodySpinPivot[0]=10;request.bodySpinPivot[1]=.95;request.bodySpinPivot[2]=2;
    const TestVector pivot{10,.95,2};
    const std::array<vr::HmdQuaternion_t,6> turns{
        vr::HmdQuaternion_t{1,0,0,0},
        {std::cos(pi/4),0,0,std::sin(pi/4)},
        {0,1,0,0},{0,-1,0,0},
        {std::cos(pi/4),0,std::sin(pi/4),0},
        Multiply(Multiply({std::cos(.2),0,std::sin(.2),0},
                          {std::cos(.3),std::sin(.3),0,0}),
                 {std::cos(.4),0,0,std::sin(.4)})};
    for (auto mode : {0u,1u})
    {
        request.requestedMode=mode;++request.epoch;
        for (uint32_t i=0;i<4;++i)
        {
            if (mode&&i<3)
            {
                request.bodySpinActive=0;
                Check(router.RoutePose(i,request,true,now,frequency,baseline[i]),"desktop unspun baseline is captured independently from the transform");
            }
            else baseline[i]=original[i];
        }
        request.bodySpinActive=1;
        for (auto turn : turns)
        {
            ++request.bodySpinGeneration;
            request.bodySpinQuaternion[0]=turn.w;request.bodySpinQuaternion[1]=turn.x;
            request.bodySpinQuaternion[2]=turn.y;request.bodySpinQuaternion[3]=turn.z;
            std::array<TestVector,4> before{},transformed{};
            for (uint32_t i=0;i<4;++i)
            {
                bool spun=false;
                Check(router.RoutePose(i,request,true,now,frequency,after,&spun)&&spun,
                      "leased spin routes each eligible head, hand and body tracker in both source modes");
                before[i]=TestWorldPosition(baseline[i]);
                transformed[i]=TestWorldPosition(after);
                const auto delta=MatrixRotate(turn,{before[i].x-pivot.x,before[i].y-pivot.y,before[i].z-pivot.z});
                Check(SameVector(transformed[i],{pivot.x+delta.x,pivot.y+delta.y,pivot.z+delta.z})&&
                      SameRotation(TestWorldRotation(after),Multiply(turn,TestWorldRotation(baseline[i]))),
                      "independent matrix oracle verifies full WORLD position and orientation around the shared pivot");
                sw::PoseSnapshot captured{};
                Check(router.Physical(i,captured)&&std::memcmp(&captured.pose,&original[i],sizeof(original[i]))==0,
                      "spin never overwrites independently captured raw physical poses or derivatives");
                if(!mode)
                    Check(std::memcmp(after.vecVelocity,original[i].vecVelocity,sizeof(after.vecVelocity))==0&&
                          SameRotation(after.qRotation,original[i].qRotation)&&
                          SameRotation(after.qDriverFromHeadRotation,original[i].qDriverFromHeadRotation),
                          "outer transform preserves physical driver-local head offsets and velocity coordinates");
            }
            for(uint32_t i=0;i<4;++i)
                for(uint32_t j=i+1;j<4;++j)
                    Check(Near(DistanceSquared(before[i],before[j]),DistanceSquared(transformed[i],transformed[j])),
                          "whole tracked-body spin preserves every inter-device distance");
            Check(!router.RoutePose(4,request,true,now,frequency,after),"tracking-reference devices remain completely untransformed");
        }
    }
    request.requestedMode=0;++request.epoch;request.armed=0;
    Check(router.RoutePose(0,request,true,now,frequency,after),"separately leased physical spin does not depend on desktop pointer arming");
    router.CountBodySpin(request.bodySpinGeneration);
    auto status=router.GetStatus(request,true,now,frequency);
    Check(status.bodySpinActive&&status.bodySpinGeneration==request.bodySpinGeneration&&
          status.bodySpinSamples==1&&Near(status.position[0],10)&&Near(status.position[1],1.7),
          "spin diagnostics report actual submitted generation while physical head WORLD telemetry stays original");
    auto invalid=request;
    for(auto kind : {0,1,2,3,4,5,6})
    {
        invalid=request;
        switch(kind)
        {
        case 0: invalid.bodySpinQuaternion[0]=std::numeric_limits<double>::quiet_NaN();break;
        case 1: invalid.bodySpinQuaternion[0]=2;break;
        case 2: invalid.bodySpinPivot[0]=std::numeric_limits<double>::infinity();break;
        case 3: invalid.bodySpinPivot[0]=10001;break;
        case 4: invalid.bodySpinLeaseQpc=0;break;
        case 5: invalid.bodySpinGeneration=0;break;
        case 6: invalid.bodySpinReserved=1;break;
        }
        Check(!sw::Router::ValidRequest(invalid,now,frequency,error)&&
              !router.RoutePose(0,invalid,true,now,frequency,after),"malformed body-spin requests fail closed before any transform");
    }
    Check(!router.RoutePose(0,request,false,now,frequency,after),"unreadable broker data cannot authorize any physical spin");
    request.timestamp=now+201000;
    Check(!router.RoutePose(0,request,true,request.timestamp,frequency,after)&&
          !router.GetStatus(request,true,request.timestamp,frequency).bodySpinActive,
          "separate spin lease expires at200ms despite a refreshed broker heartbeat");
    request.bodySpinLeaseQpc=request.timestamp;
    Check(!router.RoutePose(3,request,true,request.timestamp,frequency,after),"fresh spin lease cannot resurrect a stale body tracker");
    Check(router.OriginalForRestore(3,request.timestamp,frequency,after)&&!after.poseIsValid&&
          Near(after.vecPosition[1],original[3].vecPosition[1]),"lease recovery restores stale original geometry with an invalid tracking flag");
    router.Capture(3,original[3],request.timestamp);
    Check(sw::Router::BodySpinActive(request,true,request.timestamp,frequency)&&
          !router.RoutePose(3,request,true,request.timestamp,frequency,after)&&
          !router.GetStatus(request,true,request.timestamp,frequency).bodySpinActive,
          "fresh lease and body tracker cannot spin while the independent physical head is stale");
    router.Capture(0,original[0],request.timestamp);
    router.Capture(1,original[1],request.timestamp);
    router.Capture(2,original[2],request.timestamp);
    Check(!router.RoutePose(3,request,true,request.timestamp,frequency,after),
          "fresh complete rig recovery cannot resume a previously refused spin generation");
    ++request.bodySpinGeneration;
    Check(router.RoutePose(3,request,true,request.timestamp,frequency,after),
          "fresh complete rig and explicit new generation enable the separate spin lease");
    auto invalidHead=original[0];invalidHead.poseIsValid=false;
    router.Capture(0,invalidHead,request.timestamp);
    Check(!router.RoutePose(3,request,true,request.timestamp,frequency,after),
          "invalid head capture stops all body transforms even when tracker data remains fresh");
    router.Capture(0,original[0],request.timestamp);
    router.SetRole(0,sw::DeviceRole::Other,100);
    Check(!router.RoutePose(3,request,true,request.timestamp,frequency,after),
          "loss of independently identified head role stops leased body transforms");
    router.SetRole(0,sw::DeviceRole::Head,100);router.SetBodySpinEligible(0,true);
    router.Capture(0,original[0],request.timestamp);
    ++request.bodySpinGeneration;
    auto oldEpoch=request;--oldEpoch.epoch;
    Check(!router.RoutePose(3,oldEpoch,true,request.timestamp,frequency,after)&&
          !router.GetStatus(oldEpoch,true,request.timestamp,frequency).bodySpinActive,
          "older request epoch cannot route or falsely report an active body-spin generation");
    Check(router.OriginalForRestore(3,request.timestamp,frequency,after)&&
          std::memcmp(&after,&original[3],sizeof(after))==0,"fresh restore uses the exact unmodified original pose");
    router.SetRole(3,sw::DeviceRole::Other,203);
    Check(!router.BodySpinEligible(3)&&!router.RoutePose(3,request,true,request.timestamp,frequency,after),
          "replacement property container cannot inherit the old tracked-body eligibility");
    router.SetBodySpinEligible(3,true);
    Check(!router.RoutePose(3,request,true,request.timestamp,frequency,after),
          "replacement discovery alone cannot reuse the prior container's physical capture");
    router.Capture(3,original[3],request.timestamp);
    ++request.bodySpinGeneration;
    Check(router.RoutePose(3,request,true,request.timestamp,frequency,after),
          "new discovery plus its own fresh capture can approve the replacement physical tracker identity");
    router.SetBodySpinEligible(3,false);
    Check(!router.RoutePose(3,request,true,request.timestamp,frequency,after),"device eligibility loss immediately refuses body spin");
    request.bodySpinActive=0;
    Check(!router.RoutePose(3,request,true,request.timestamp,frequency,after),"spin release returns generic trackers to physical passthrough");
    alignas(8) std::byte block[512]{};
    request.bodySpinActive=1;
    sw::WriteBlock(block,request);sw::Request roundtrip{};
    Check(sw::ReadBlock(block,roundtrip)&&roundtrip.bodySpinGeneration==request.bodySpinGeneration&&
          roundtrip.bodySpinLeaseQpc==request.bodySpinLeaseQpc&&roundtrip.bodySpinPivot[1]==.95,
          "extended quaternion/pivot/lease share the existing bounded atomic512-byte request transaction");
}
