#include "ipc.hpp"
#include "openvr.h"
#include <bcrypt.h>
#include <cmath>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <regex>
#include <sstream>
#include <string>
#include <tlhelp32.h>
#include <vector>
namespace
{
bool RuntimeActive(DWORD *pid = nullptr)
{
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE)
        return false;
    PROCESSENTRY32W entry{};
    entry.dwSize = sizeof(entry);
    bool active = false;
    if (Process32FirstW(snapshot, &entry))
        do
        {
            if (_wcsicmp(entry.szExeFile, L"vrserver.exe") == 0)
            {
                active = true;
                if (pid)
                    *pid = entry.th32ProcessID;
                break;
            }
        } while (Process32NextW(snapshot, &entry));
    CloseHandle(snapshot);
    return active;
}
std::string Json(const std::string &s)
{
    std::ostringstream out;
    out << '"';
    for (unsigned char c : s)
    {
        switch (c)
        {
        case '"':
            out << "\\\"";
            break;
        case '\\':
            out << "\\\\";
            break;
        case '\n':
            out << "\\n";
            break;
        case '\r':
            out << "\\r";
            break;
        case '\t':
            out << "\\t";
            break;
        default:
            if (c < 32)
                out << "?";
            else
                out << c;
        }
    }
    out << '"';
    return out.str();
}
std::string Hash(const std::string &value)
{
    BCRYPT_ALG_HANDLE algorithm{};
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0)
        return "hash-unavailable";
    unsigned char digest[32]{};
    auto salt = sw::UserSid();
    std::string input;
    for (auto c : salt)
        input += static_cast<char>(c);
    input += ':';
    input += value;
    auto status = BCryptHash(algorithm, nullptr, 0, reinterpret_cast<PUCHAR>(input.data()),
                             static_cast<ULONG>(input.size()), digest, 32);
    BCryptCloseAlgorithmProvider(algorithm, 0);
    if (status < 0)
        return "hash-unavailable";
    std::ostringstream out;
    for (auto c : digest)
        out << std::hex << std::setw(2) << std::setfill('0') << unsigned(c);
    return out.str();
}
std::string Property(vr::IVRSystem *s, uint32_t index, vr::ETrackedDeviceProperty key)
{
    vr::ETrackedPropertyError error{};
    char text[512]{};
    s->GetStringTrackedDeviceProperty(index, key, text, sizeof(text), &error);
    return error == vr::TrackedProp_Success ? text : "";
}
void Emit(const std::string &json, const std::string &file)
{
    if (file.empty())
        std::cout << json << '\n';
    else
    {
        std::ofstream out(file, std::ios::binary);
        out << json << '\n';
        if (!out)
        {
            std::cerr << "Cannot write report\n";
            std::exit(3);
        }
        std::cout << "Wrote redacted diagnostics\n";
    }
}
std::string RuntimeBuild(DWORD pid)
{
    auto process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!process)
        return "unavailable";
    wchar_t path[32768]{};
    DWORD bytes = 32768;
    bool queried = QueryFullProcessImageNameW(process, 0, path, &bytes) != 0;
    CloseHandle(process);
    if (!queried)
        return "unavailable";
    for (auto dir = std::filesystem::path(path).parent_path(); !dir.empty();
         dir = dir.parent_path())
    {
        std::ifstream manifest(dir / L"appmanifest_250820.acf", std::ios::binary);
        std::string text{std::istreambuf_iterator<char>(manifest),
                         std::istreambuf_iterator<char>()};
        std::smatch match;
        if (std::regex_search(text, match, std::regex("\\\"buildid\\\"\\s*\\\"([0-9]+)\\\"")))
            return match[1];
        if (dir == dir.parent_path())
            break;
    }
    return "unavailable";
}
void Number(std::ostream &out, double value)
{
    if (std::isfinite(value))
        out << std::setprecision(17) << value;
    else
        out << "null";
}
void Values(std::ostream &out, const double *values, size_t count)
{
    out << '[';
    for (size_t i = 0; i < count; ++i)
    {
        if (i)
            out << ',';
        Number(out, values[i]);
    }
    out << ']';
}
int Record(vr::IVRSystem *system, sw::Mapping &mapping, const std::string &file, int seconds,
           int hz, DWORD serverPid)
{
    std::ofstream out(file, std::ios::binary);
    if (!out)
    {
        std::cerr << "Cannot write capture file\n";
        return 3;
    }
    const auto frequency = sw::QpcFrequency(), start = sw::QpcNow();
    out << "{\"event\":\"header\",\"schemaVersion\":1,\"evidence\":\"observed-live-background-"
           "sampling\",\"openvrCommit\":\"0924064316de3effbcd1acf1e309182a2deb1c05\","
           "\"sdkVersion\":\""
        << vr::k_nSteamVRVersionMajor << '.' << vr::k_nSteamVRVersionMinor << '.'
        << vr::k_nSteamVRVersionBuild << "\",\"runtimeBuildId\":" << Json(RuntimeBuild(serverPid))
        << ",\"serverProcessId\":" << serverPid << ",\"qpcFrequency\":" << frequency
        << ",\"seconds\":" << seconds << ",\"hz\":" << hz
        << ",\"predictionSeconds\":0,\"clientCoordinates\":\"TrackingUniverseStanding post-routing "
           "output\",\"driverCoordinates\":\"DriverPose world-from-driver and driver-from-head "
           "transformed pre-output capture; standing/chaperone transform may "
           "differ\",\"syntheticCoordinates\":\"Actually forwarded synthetic head in DriverPose "
           "world coordinates; standing/chaperone transform may differ\","
           "\"provenance\":\"Driver capture trustworthy only if protocol valid, pose hook "
           "capability present, sample count advances, and driver heartbeat/head fresh. Real "
           "source independence must be established by distinct "
           "movement.\",\"noSerialsOrSceneIdentifiers\":true}\n";
    for (int sample = 0; sample < seconds * hz; ++sample)
    {
        auto deadline = start + int64_t(sample) * frequency / hz;
        auto remaining = deadline - sw::QpcNow();
        if (remaining > 0)
            Sleep(DWORD((remaining * 1000 + frequency - 1) / frequency));
        DWORD currentPid{};
        if (!RuntimeActive(&currentPid) || currentPid != serverPid)
        {
            out << "{\"event\":\"runtime-lost-or-changed\",\"sample\":" << sample << "}\n";
            break;
        }
        const auto now = sw::QpcNow();
        vr::TrackedDevicePose_t poses[vr::k_unMaxTrackedDeviceCount]{};
        system->GetDeviceToAbsoluteTrackingPose(vr::TrackingUniverseStanding, 0, poses,
                                                vr::k_unMaxTrackedDeviceCount);
        auto &head = poses[0];
        sw::Status status{};
        bool read = sw::ReadBlock(mapping.status(), status) && status.magic == sw::Magic &&
                    status.version == sw::Version;
        const auto statusReadQpc = sw::QpcNow();
        auto statusAge = read ? (static_cast<double>(statusReadQpc) - status.timestamp) * 1000 /
                                   frequency : -1.;
        bool hook = read && (status.reserved0 & 1) && status.physicalSamples > 0;
        bool fresh = read && statusAge >= -25 && statusAge <= sw::WatchdogMilliseconds;
        bool captured = hook && fresh && status.hasHead && status.headAgeMilliseconds >= 0 &&
                        status.headAgeMilliseconds <= sw::WatchdogMilliseconds;
        auto syntheticAge = status.syntheticHeadQpc
                                ? (static_cast<double>(statusReadQpc) - status.syntheticHeadQpc) *
                                      1000 / frequency : -1.;
        bool synthetic = hook && fresh && (status.actualMode == 1 || status.bodySpinActive) && status.syntheticHeadValid &&
                         status.syntheticHeadEpoch == status.ackEpoch &&
                         (status.actualMode != 1 || status.anchorEpoch == status.ackEpoch) && status.syntheticHeadQpc > 0 &&
                         syntheticAge >= -25 && syntheticAge <= sw::WatchdogMilliseconds;
        out << "{\"event\":\"sample\",\"sample\":" << sample << ",\"qpc\":" << now
            << ",\"sceneProcessId\":"
            << (vr::VRApplications() ? vr::VRApplications()->GetCurrentSceneProcessId() : 0)
            << ",\"clientHead\":{\"connected\":" << (head.bDeviceIsConnected ? "true" : "false")
            << ",\"poseValid\":" << (head.bPoseIsValid ? "true" : "false")
            << ",\"trackingResult\":" << int(head.eTrackingResult) << ",\"standingMatrix34\":[";
        for (int row = 0; row < 3; ++row)
            for (int col = 0; col < 4; ++col)
            {
                if (row || col)
                    out << ',';
                Number(out, head.mDeviceToAbsoluteTracking.m[row][col]);
            }
        out << "]},\"driverCapture\":{\"protocolValid\":" << (read ? "true" : "false")
            << ",\"poseHookPresent\":" << (hook ? "true" : "false")
            << ",\"statusFresh\":" << (fresh ? "true" : "false")
            << ",\"capturedHeadValid\":" << (captured ? "true" : "false")
            << ",\"statusAgeMs\":";
        Number(out, statusAge);
        out << ",\"driverQpc\":" << status.timestamp << ",\"headAgeMs\":";
        Number(out, status.headAgeMilliseconds);
        out << ",\"actualMode\":" << status.actualMode << ",\"error\":" << status.error
            << ",\"ackEpoch\":" << status.ackEpoch
            << ",\"anchorEpoch\":" << status.anchorEpoch
            << ",\"inputCoverage\":" << status.inputCoverage
            << ",\"selectedMenuPath\":" << status.selectedMenuPath
            << ",\"menuRisingEdges\":" << status.menuRisingEdges
            << ",\"menuPressed\":" << status.menuPressed
            << ",\"lastInputError\":" << status.lastInputError
            << ",\"effectiveNativeActions\":" << status.effectiveNativeActions
            << ",\"inputArmed\":" << status.inputArmed
            << ",\"bodySpinActive\":" << status.bodySpinActive
            << ",\"bodySpinGeneration\":" << status.bodySpinGeneration
            << ",\"bodySpinSamples\":" << status.bodySpinSamples
            << ",\"proximityKnown\":" << status.proximityKnown
            << ",\"proximityActive\":" << status.proximityActive
            << ",\"proximityAgeMs\":";
        Number(out, status.proximityAgeMilliseconds);
        out << ",\"leftPhysicalAgeMs\":";
        Number(out, status.leftPhysicalAgeMilliseconds);
        out << ",\"rightPhysicalAgeMs\":";
        Number(out, status.rightPhysicalAgeMilliseconds);
        out << ",\"physicalSamples\":" << status.physicalSamples
            << ",\"routedSamples\":" << status.routedSamples << ",\"capturedWorldPosition\":";
        if (captured)
            Values(out, status.position, 3);
        else
            out << "null";
        out << ",\"capturedWorldQuaternionWxyz\":";
        if (captured)
            Values(out, status.quaternion, 4);
        else
            out << "null";
        out << ",\"routedSyntheticHeadValid\":" << (synthetic ? "true" : "false")
            << ",\"syntheticHeadEpoch\":" << status.syntheticHeadEpoch
            << ",\"syntheticHeadQpc\":" << status.syntheticHeadQpc
            << ",\"syntheticHeadAgeMs\":";
        Number(out, syntheticAge);
        out << ",\"routedSyntheticWorldPosition\":";
        if (synthetic)
            Values(out, status.syntheticPosition, 3);
        else
            out << "null";
        out << ",\"routedSyntheticWorldQuaternionWxyz\":";
        if (synthetic)
            Values(out, status.syntheticQuaternion, 4);
        else
            out << "null";
        out << "}}\n";
        if (!out)
            return 3;
    }
    out << "{\"event\":\"end\",\"qpc\":" << sw::QpcNow() << ",\"noHardwarePassInferred\":true}\n";
    std::cout << "Wrote bounded physical/output comparison capture\n";
    return 0;
}
bool ParseInteger(const char *text, int &value)
{
    char *end{};
    long parsed = std::strtol(text, &end, 10);
    if (!text[0] || *end || parsed < 1 || parsed > 120)
        return false;
    value = int(parsed);
    return true;
}
} // namespace
int Run(int argc, char **argv)
{
    std::string output, record;
    bool ipcOnly = false;
    int seconds = 15, hz = 30;
    for (int i = 1; i < argc; ++i)
    {
        std::string arg = argv[i];
        if (arg == "--output" && i + 1 < argc)
            output = argv[++i];
        else if (arg == "--record" && i + 1 < argc)
            record = argv[++i];
        else if (arg == "--seconds" && i + 1 < argc)
        {
            if (!ParseInteger(argv[++i], seconds) || seconds > 60)
            {
                std::cerr << "seconds must be 1..60\n";
                return 3;
            }
        }
        else if (arg == "--hz" && i + 1 < argc)
        {
            if (!ParseInteger(argv[++i], hz))
            {
                std::cerr << "hz must be 1..120\n";
                return 3;
            }
        }
        else if (arg == "--ipc")
            ipcOnly = true;
        else if (arg == "--help")
        {
            std::cout
                << "switcheroonie-diagnostics [--ipc] [--output "
                   "file.json]\nswitcheroonie-diagnostics --record file.jsonl [--seconds 15] [--hz "
                   "30]\nRead-only. Background client connection occurs only when vrserver already "
                   "exists. Never launches SteamVR. Record compares driver pre-output capture with "
                   "client standing output; coordinate origins can differ.\n";
            return 0;
        }
        else
        {
            std::cerr << "Unknown option\n";
            return 3;
        }
    }
    if (ipcOnly && !record.empty())
    {
        std::cerr << "--ipc and --record cannot be combined\n";
        return 3;
    }
    std::ostringstream report;
    report << "{\"schemaVersion\":1,\"evidence\":\"observed-live-client\",\"initializationType\":"
              "\"VRApplication_Background\",\"startsRuntime\":false,";
    sw::Mapping mapping;
    sw::Status status{};
    bool ipc = mapping.Open(false) && sw::ReadBlock(mapping.status(), status) &&
               status.magic == sw::Magic && status.version == sw::Version;
    report << "\"ipcAvailable\":" << (ipc ? "true" : "false");
    if (ipc)
    {
        report << ",\"driverStatus\":{\"mode\":" << status.actualMode
               << ",\"error\":" << status.error << ",\"headAgeMs\":";
        Number(report, status.headAgeMilliseconds);
        report << ",\"physicalSamples\":" << status.physicalSamples
               << ",\"routedSamples\":" << status.routedSamples
               << ",\"ackEpoch\":" << status.ackEpoch
               << ",\"anchorEpoch\":" << status.anchorEpoch
               << ",\"syntheticHeadEpoch\":" << status.syntheticHeadEpoch
               << ",\"syntheticHeadValid\":" << status.syntheticHeadValid
               << ",\"syntheticHeadQpc\":" << status.syntheticHeadQpc
               << ",\"inputCoverage\":" << status.inputCoverage
               << ",\"selectedMenuPath\":" << status.selectedMenuPath
               << ",\"menuRisingEdges\":" << status.menuRisingEdges
               << ",\"menuPressed\":" << status.menuPressed
               << ",\"lastInputError\":" << status.lastInputError
               << ",\"effectiveNativeActions\":" << status.effectiveNativeActions
               << ",\"inputArmed\":" << status.inputArmed
               << ",\"bodySpinActive\":" << status.bodySpinActive
               << ",\"bodySpinGeneration\":" << status.bodySpinGeneration
               << ",\"bodySpinSamples\":" << status.bodySpinSamples
               << ",\"proximityKnown\":" << status.proximityKnown
               << ",\"proximityActive\":" << status.proximityActive
               << ",\"proximityAgeMs\":";
        Number(report, status.proximityAgeMilliseconds);
        report << ",\"leftPhysicalAgeMs\":";
        Number(report, status.leftPhysicalAgeMilliseconds);
        report << ",\"rightPhysicalAgeMs\":";
        Number(report, status.rightPhysicalAgeMilliseconds);
        report << "}";
    }
    if (ipcOnly)
    {
        report << ",\"runtimeProbed\":false}";
        Emit(report.str(), output);
        return 0;
    }
    DWORD serverPid{};
    if (!RuntimeActive(&serverPid))
    {
        report << ",\"runtimeActive\":false,\"runtimeProbed\":false,\"devices\":[],\"limitation\":"
                  "\"No vrserver exists; no OpenVR initialization or F1 recording attempted.\"}";
        Emit(report.str(), output);
        return 2;
    }
    vr::EVRInitError error{};
    auto *system = vr::VR_Init(&error, vr::VRApplication_Background);
    if (error != vr::VRInitError_None || !system)
    {
        report << ",\"runtimeActive\":true,\"runtimeProbed\":true,\"initError\":"
               << Json(vr::VR_GetVRInitErrorAsEnglishDescription(error)) << "}";
        Emit(report.str(), output);
        return 2;
    }
    report << ",\"runtimeActive\":true,\"runtimeProbed\":true,\"hmdPresent\":"
           << (vr::VR_IsHmdPresent() ? "true" : "false") << ",\"devices\":[";
    bool first = true;
    vr::TrackedDevicePose_t poses[vr::k_unMaxTrackedDeviceCount]{};
    system->GetDeviceToAbsoluteTrackingPose(vr::TrackingUniverseStanding, 0, poses,
                                            vr::k_unMaxTrackedDeviceCount);
    for (uint32_t i = 0; i < vr::k_unMaxTrackedDeviceCount; ++i)
    {
        auto type = system->GetTrackedDeviceClass(i);
        if (type == vr::TrackedDeviceClass_Invalid)
            continue;
        if (!first)
            report << ',';
        first = false;
        auto serial = Property(system, i, vr::Prop_SerialNumber_String);
        report << "{\"index\":" << i << ",\"class\":" << int(type)
               << ",\"role\":" << int(system->GetControllerRoleForTrackedDeviceIndex(i))
               << ",\"connected\":" << (poses[i].bDeviceIsConnected ? "true" : "false")
               << ",\"poseValid\":" << (poses[i].bPoseIsValid ? "true" : "false")
               << ",\"serialHash\":" << Json(Hash(serial)) << ",\"trackingSystem\":"
               << Json(Property(system, i, vr::Prop_TrackingSystemName_String))
               << ",\"manufacturer\":"
               << Json(Property(system, i, vr::Prop_ManufacturerName_String))
               << ",\"model\":" << Json(Property(system, i, vr::Prop_ModelNumber_String))
               << ",\"driverVersion\":" << Json(Property(system, i, vr::Prop_DriverVersion_String))
               << "}";
    }
    report << "],\"sourceLimitation\":\"Client poses are post-routing. Independent physical "
              "provenance requires the experimental server hook and real-headset F1 "
              "test.\",\"sceneProcessId\":"
           << (vr::VRApplications() ? vr::VRApplications()->GetCurrentSceneProcessId() : 0) << "}";
    Emit(report.str(), output);
    int result = record.empty() ? 0 : Record(system, mapping, record, seconds, hz, serverPid);
    vr::VR_Shutdown();
    return result;
}
int main(int argc, char **argv)
{
    try
    {
        return Run(argc, argv);
    }
    catch (const std::exception &e)
    {
        std::cerr << "Diagnostics failed: " << e.what() << '\n';
        vr::VR_Shutdown();
        return 3;
    }
    catch (...)
    {
        std::cerr << "Diagnostics failed\n";
        vr::VR_Shutdown();
        return 3;
    }
}
