// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma once

#include <optional>
#include <string>
#include <string_view>

namespace desktop
{
inline std::optional<bool> AppearanceDark(std::wstring_view message)
{
    if (message == L"appearance:system:dark" || message == L"appearance:dark:dark") return true;
    if (message == L"appearance:system:light" || message == L"appearance:light:light") return false;
    return std::nullopt;
}

inline std::optional<std::wstring> LoopbackOrigin(std::wstring_view value)
{
    constexpr std::wstring_view prefix = L"http://127.0.0.1:";
    if (!value.starts_with(prefix)) return std::nullopt;
    auto port = value.substr(prefix.size());
    if (port.ends_with(L"/")) port.remove_suffix(1);
    if (port.empty() || port.size() > 5) return std::nullopt;
    unsigned number = 0;
    for (const auto digit : port)
    {
        if (digit < L'0' || digit > L'9') return std::nullopt;
        number = number * 10 + static_cast<unsigned>(digit - L'0');
    }
    if (number == 0 || number > 65535) return std::nullopt;
    return std::wstring(prefix) + std::wstring(port);
}

inline bool SameOrigin(std::wstring_view value, std::wstring_view origin)
{
    if (!value.starts_with(origin)) return false;
    if (value.size() == origin.size()) return true;
    const auto delimiter = value[origin.size()];
    return delimiter == L'/' || delimiter == L'?' || delimiter == L'#';
}

inline bool ExternalUrl(std::wstring_view value)
{
    for (const auto c : value)
    {
        if (c <= L' ' || c == L'\\' || c == 127) return false;
    }
    constexpr std::wstring_view session = L"ghapp://session/new";
    if (value == session || (value.starts_with(session) && value[session.size()] == L'?'))
    {
        return true;
    }
    constexpr std::wstring_view https = L"https://";
    if (!value.starts_with(https)) return false;
    const auto end = value.find_first_of(L"/?#", https.size());
    const auto authority = value.substr(https.size(), end == value.npos ? value.npos : end - https.size());
    return !authority.empty() && authority.find_first_of(L"@%") == authority.npos;
}

inline std::optional<unsigned long> ProcessId(std::wstring_view value)
{
    if (value.empty() || value.size() > 10) return std::nullopt;
    unsigned long long number = 0;
    for (const auto digit : value)
    {
        if (digit < L'0' || digit > L'9') return std::nullopt;
        number = number * 10 + static_cast<unsigned>(digit - L'0');
    }
    if (number <= 1 || number > 0xffffffffULL) return std::nullopt;
    return static_cast<unsigned long>(number);
}

// CreateProcess receives one command line; quote according to CommandLineToArgvW,
// never via cmd.exe or a shell.
inline std::wstring QuoteArgument(std::wstring_view argument)
{
    std::wstring result = L"\"";
    std::size_t slashes = 0;
    for (const auto c : argument)
    {
        if (c == L'\\') { ++slashes; continue; }
        if (c == L'"')
        {
            result.append(slashes * 2 + 1, L'\\');
        }
        else
        {
            result.append(slashes, L'\\');
        }
        slashes = 0;
        result += c;
    }
    result.append(slashes * 2, L'\\');
    return result + L'"';
}
}
