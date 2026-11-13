#pragma once
#include <string>
#include <string_view>
namespace sw
{
struct DriverConfig
{
    bool experimentalOptIn{};
    std::string approvedRuntimeBuild;
};
// Strict small schema parser: unknown keys, duplicates, comments, invalid tokens,
// escapes in this ASCII schema, and trailing data all fail closed.
inline bool ParseConfig(std::string_view text, DriverConfig &out)
{
    size_t at = 0;
    if (text.starts_with("\xEF\xBB\xBF"))
        at = 3;
    auto whitespace = [&] {
        while (at < text.size() &&
               (text[at] == ' ' || text[at] == '\t' || text[at] == '\r' || text[at] == '\n'))
            ++at;
    };
    auto consume = [&](char c) {
        whitespace();
        if (at >= text.size() || text[at] != c)
            return false;
        ++at;
        return true;
    };
    auto string = [&](std::string &value) {
        if (!consume('"'))
            return false;
        value.clear();
        while (at < text.size() && text[at] != '"')
        {
            auto c = text[at++];
            if (c < 32 || c == '\\')
                return false;
            value += c;
        }
        if (at >= text.size())
            return false;
        ++at;
        return true;
    };
    DriverConfig parsed;
    bool sawOptIn = false, sawBuild = false;
    if (!consume('{'))
        return false;
    while (true)
    {
        std::string key;
        if (!string(key) || !consume(':'))
            return false;
        whitespace();
        if (key == "experimentalOptIn")
        {
            if (sawOptIn)
                return false;
            sawOptIn = true;
            if (text.substr(at, 4) == "true")
            {
                parsed.experimentalOptIn = true;
                at += 4;
            }
            else if (text.substr(at, 5) == "false")
            {
                parsed.experimentalOptIn = false;
                at += 5;
            }
            else
                return false;
        }
        else if (key == "approvedRuntimeBuild")
        {
            if (sawBuild || !string(parsed.approvedRuntimeBuild) ||
                parsed.approvedRuntimeBuild.empty() || parsed.approvedRuntimeBuild.size() > 20)
                return false;
            sawBuild = true;
            for (char c : parsed.approvedRuntimeBuild)
                if (c < '0' || c > '9')
                    return false;
        }
        else
            return false;
        whitespace();
        if (at < text.size() && text[at] == '}')
        {
            ++at;
            break;
        }
        if (!consume(','))
            return false;
    }
    whitespace();
    if (at != text.size() || !sawOptIn || !sawBuild)
        return false;
    out = std::move(parsed);
    return true;
}
} // namespace sw
