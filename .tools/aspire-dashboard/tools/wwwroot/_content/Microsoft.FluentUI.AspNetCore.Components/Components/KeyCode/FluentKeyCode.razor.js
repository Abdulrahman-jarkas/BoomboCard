export function RegisterKeyCode(globalDocument, eventNames, id, elementRef, onlyCodes, excludeCodes, stopPropagation, preventDefault, preventDefaultOnly, dotNetHelper, preventMultipleKeydown, stopRepeat) {
    const element = globalDocument
                  ? document
                  : elementRef == null ? document.getElementById(id) : elementRef;

    if (document.fluentKeyCodeEvents == null) {
        document.fluentKeyCodeEvents = {};
    }

    if (!!element) {

        const eventId = Math.random().toString(36).slice(2);
        let fired = false;

        const handlerKeydown = function (e) {
            if (!fired || !preventMultipleKeydown) {
                fired = true;
                return handler(e, "OnKeyDownRaisedAsync");
            }
        }

        const handlerKeyup = function (e) {
            fired = false;
            return handler(e, "OnKeyUpRaisedAsync");
        }

        const handler = function (e, netMethod) {
            const keyCode = e.which || e.keyCode || e.charCode;

            if (stopRepeat && e.repeat) {
                return;
            }

            if (!!dotNetHelper && !!dotNetHelper.invokeMethodAsync) {

                const targetId = e.currentTarget?.id ?? "";
                const isPreventDefault = preventDefault || (preventDefaultOnly.length > 0 && preventDefaultOnly.includes(keyCode));
                const isStopPropagation = stopPropagation;

                // Exclude
                if (excludeCodes.length > 0 && excludeCodes.includes(keyCode)) {
                    if (isPreventDefault) {
                        e.preventDefault();
                    }
                    if (isStopPropagation) {
                        e.stopPropagation();
                    }
                    return;
                }

                // All or Include only
                if (onlyCodes.length == 0 || (onlyCodes.length > 0 && onlyCodes.includes(keyCode))) {
                    if (isPreventDefault) {
                        e.preventDefault();
                    }
                    if (isStopPropagation) {
                        e.stopPropagation();
                    }
                    dotNetHelper.invokeMethodAsync(netMethod, keyCode, e.key, e.ctrlKey, e.shiftKey, e.altKey, e.metaKey, e.location, targetId, e.repeat);
                    return;
                }
            }
        };

        if (preventMultipleKeydown || (!!eventNames && eventNames.includes("KeyDown"))) {
            element.addEventListener('keydown', handlerKeydown)
        }
        if (preventMultipleKeydown || (!!eventNames && eventNames.includes("KeyUp"))) {
            element.addEventListener('keyup', handlerKeyup)
        }
        document.fluentKeyCodeEvents[eventId] = { source: element, handlerKeydown, handlerKeyup };

        return eventId;
    }

    return "";
}

export function UnregisterKeyCode(eventId) {

    if (document.fluentKeyCodeEvents != null) {
        const keyEvent = document.fluentKeyCodeEvents[eventId];
        const element = keyEvent.source;

        if (!!keyEvent.handlerKeydown) {
            element.removeEventListener("keydown", keyEvent.handlerKeydown);
        }

        if (!!keyEvent.handlerKeyup) {
            element.removeEventListener("keyup", keyEvent.handlerKeyup);
        }

        delete document.fluentKeyCodeEvents[eventId];
    }
}

// SIG // Begin signature block
// SIG // MIIpGQYJKoZIhvcNAQcCoIIpCjCCKQYCAQExDzANBglg
// SIG // hkgBZQMEAgEFADB3BgorBgEEAYI3AgEEoGkwZzAyBgor
// SIG // BgEEAYI3AgEeMCQCAQEEEBDgyQbOONQRoqMAEEvTUJAC
// SIG // AQACAQACAQACAQACAQAwMTANBglghkgBZQMEAgEFAAQg
// SIG // cM7jAjUKG+ldPK+a4omxx88BLRsrRYQFbz8jysFQB2yg
// SIG // gg3lMIIGYzCCBEugAwIBAgITMwAABEAU/AvoPvEkXwAA
// SIG // AAAEQDANBgkqhkiG9w0BAQwFADB+MQswCQYDVQQGEwJV
// SIG // UzETMBEGA1UECBMKV2FzaGluZ3RvbjEQMA4GA1UEBxMH
// SIG // UmVkbW9uZDEeMBwGA1UEChMVTWljcm9zb2Z0IENvcnBv
// SIG // cmF0aW9uMSgwJgYDVQQDEx9NaWNyb3NvZnQgQ29kZSBT
// SIG // aWduaW5nIFBDQSAyMDExMB4XDTI0MTExOTE5NTEwMFoX
// SIG // DTI1MTExMjE5NTEwMFowYzELMAkGA1UEBhMCVVMxEzAR
// SIG // BgNVBAgTCldhc2hpbmd0b24xEDAOBgNVBAcTB1JlZG1v
// SIG // bmQxHjAcBgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlv
// SIG // bjENMAsGA1UEAxMELk5FVDCCAaIwDQYJKoZIhvcNAQEB
// SIG // BQADggGPADCCAYoCggGBAMKeXYqEhaQPQ4ZCnfTl1lW6
// SIG // APVSCobvBFkZfqsIvta0p/RSaw5GeP5VHQjjs+qM7uwu
// SIG // c6bCMfnp+u4krLIDsXHzNYgg05gHoQqhM7E57/FiexSo
// SIG // BSZz4BHIswq3zBspkW77XkI3MgjVoM/K/WWDrC3v/+Tj
// SIG // CRY1lQNXRKXpLIAMlInJ0WJ3VoE7WAyPYIsu+ek/susT
// SIG // PxF2nd+C/MMA7shOHkFipk022XuR7uScnz8e0YKNR54q
// SIG // OxfoG/3VrQiU5V1rz0JvOjQ+xH9zVny40ig+0HflfDGV
// SIG // dtwb4wPw/IukGijJ1J3GblnLKq8bp/czV2+w5b6MGit5
// SIG // 8G/1RizEsw82Ptur4bzXzJCIwlFe68La6aw5dQ2s0iTT
// SIG // mWHCXyRoR2yiryqe3zJq1ggr2l6hvtVWpg/QuFXIv29b
// SIG // 87Pnzs4W+Us6ZWejAty+dznDLgqYrKmXP7smKR38G+4f
// SIG // SAzGnqn5FQMb1YPB9/uIM7YxydEcajv3xM1hqQd1d1pn
// SIG // gnJgowIDAQABo4IBczCCAW8wHwYDVR0lBBgwFgYKKwYB
// SIG // BAGCN0wIAQYIKwYBBQUHAwMwHQYDVR0OBBYEFI9QfUUX
// SIG // wy1XEVcInVlIqRRnFdv9MEUGA1UdEQQ+MDykOjA4MR4w
// SIG // HAYDVQQLExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xFjAU
// SIG // BgNVBAUTDTQ2NDIyMys1MDMzODIwHwYDVR0jBBgwFoAU
// SIG // SG5k5VAF04KqFzc3IrVtqMp1ApUwVAYDVR0fBE0wSzBJ
// SIG // oEegRYZDaHR0cDovL3d3dy5taWNyb3NvZnQuY29tL3Br
// SIG // aW9wcy9jcmwvTWljQ29kU2lnUENBMjAxMV8yMDExLTA3
// SIG // LTA4LmNybDBhBggrBgEFBQcBAQRVMFMwUQYIKwYBBQUH
// SIG // MAKGRWh0dHA6Ly93d3cubWljcm9zb2Z0LmNvbS9wa2lv
// SIG // cHMvY2VydHMvTWljQ29kU2lnUENBMjAxMV8yMDExLTA3
// SIG // LTA4LmNydDAMBgNVHRMBAf8EAjAAMA0GCSqGSIb3DQEB
// SIG // DAUAA4ICAQCdlnkKj5cRjU7MvciHbh9/o4gW2iyDjz/N
// SIG // IpnsmrAI1O9Ofcu+1UfnXRlNC30c+gg3cEPwN6NtkL5A
// SIG // qqTkEWPdf23Aj5tTXj/krK9EtYXIphc2FQS4FlquyqXu
// SIG // lk1vAuQ7O2GyAtbYf1myjgGnwHyt66X6FAsjalDv5ahh
// SIG // At/BClMF2rA6OPUMvsVRPJFn4TcmgVBN8LL4fzxAZ9US
// SIG // K/Xz/g8FT1f7+KeZ+Ot6q//WybZyV2gpPp6Vk2xKS9Of
// SIG // TeOLiT9QfH+o5rLarcanYqZ5nVVlC3d83+lWGpAK+vBc
// SIG // OmkJo/bYp8YYRKZvoDLYFNi5l3bD/i84S5j0Hda3OINQ
// SIG // aWrKF5WH3bAJscVeOCZBYmNCrh7Sgu/0fWYFkhTJut/u
// SIG // EU8uHGT99x4MoW6CAv8VGzhT4q+gAPXSzXRjFpKxlrpm
// SIG // lPgNPfug1LD96i/5W+FF5bMivfQTLLcS4enqIn02HHlQ
// SIG // w8Z+qqgjxinHsZGXVNnh5bgJIe7RRiEs3OsWZtkriMCj
// SIG // 55YMaxMCS8pwjF4bB1L9rrgy4SctMEDuoXCW2zddY2HA
// SIG // mXSThEENtm6QopQiVpJ5jq3+hHpXZ7K/3UVgbO4ixVXn
// SIG // kep3rw2xdhspjw/aoY9T8a8sCoYl1a6/X6bHmTr95sgs
// SIG // BjEKqYxpldRaRmKHSCMzEuoyLIdBZLty3jCCB3owggVi
// SIG // oAMCAQICCmEOkNIAAAAAAAMwDQYJKoZIhvcNAQELBQAw
// SIG // gYgxCzAJBgNVBAYTAlVTMRMwEQYDVQQIEwpXYXNoaW5n
// SIG // dG9uMRAwDgYDVQQHEwdSZWRtb25kMR4wHAYDVQQKExVN
// SIG // aWNyb3NvZnQgQ29ycG9yYXRpb24xMjAwBgNVBAMTKU1p
// SIG // Y3Jvc29mdCBSb290IENlcnRpZmljYXRlIEF1dGhvcml0
// SIG // eSAyMDExMB4XDTExMDcwODIwNTkwOVoXDTI2MDcwODIx
// SIG // MDkwOVowfjELMAkGA1UEBhMCVVMxEzARBgNVBAgTCldh
// SIG // c2hpbmd0b24xEDAOBgNVBAcTB1JlZG1vbmQxHjAcBgNV
// SIG // BAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjEoMCYGA1UE
// SIG // AxMfTWljcm9zb2Z0IENvZGUgU2lnbmluZyBQQ0EgMjAx
// SIG // MTCCAiIwDQYJKoZIhvcNAQEBBQADggIPADCCAgoCggIB
// SIG // AKvw+nIQHC6t2G6qghBNNLrytlghn0IbKmvpWlCquAY4
// SIG // GgRJun/DDB7dN2vGEtgL8DjCmQawyDnVARQxQtOJDXlk
// SIG // h36UYCRsr55JnOloXtLfm1OyCizDr9mpK656Ca/XllnK
// SIG // YBoF6WZ26DJSJhIv56sIUM+zRLdd2MQuA3WraPPLbfM6
// SIG // XKEW9Ea64DhkrG5kNXimoGMPLdNAk/jj3gcN1Vx5pUkp
// SIG // 5w2+oBN3vpQ97/vjK1oQH01WKKJ6cuASOrdJXtjt7UOR
// SIG // g9l7snuGG9k+sYxd6IlPhBryoS9Z5JA7La4zWMW3Pv4y
// SIG // 07MDPbGyr5I4ftKdgCz1TlaRITUlwzluZH9TupwPrRkj
// SIG // hMv0ugOGjfdf8NBSv4yUh7zAIXQlXxgotswnKDglmDlK
// SIG // Ns98sZKuHCOnqWbsYR9q4ShJnV+I4iVd0yFLPlLEtVc/
// SIG // JAPw0XpbL9Uj43BdD1FGd7P4AOG8rAKCX9vAFbO9G9RV
// SIG // S+c5oQ/pI0m8GLhEfEXkwcNyeuBy5yTfv0aZxe/CHFfb
// SIG // g43sTUkwp6uO3+xbn6/83bBm4sGXgXvt1u1L50kppxMo
// SIG // pqd9Z4DmimJ4X7IvhNdXnFy/dygo8e1twyiPLI9AN0/B
// SIG // 4YVEicQJTMXUpUMvdJX3bvh4IFgsE11glZo+TzOE2rCI
// SIG // F96eTvSWsLxGoGyY0uDWiIwLAgMBAAGjggHtMIIB6TAQ
// SIG // BgkrBgEEAYI3FQEEAwIBADAdBgNVHQ4EFgQUSG5k5VAF
// SIG // 04KqFzc3IrVtqMp1ApUwGQYJKwYBBAGCNxQCBAweCgBT
// SIG // AHUAYgBDAEEwCwYDVR0PBAQDAgGGMA8GA1UdEwEB/wQF
// SIG // MAMBAf8wHwYDVR0jBBgwFoAUci06AjGQQ7kUBU7h6qfH
// SIG // MdEjiTQwWgYDVR0fBFMwUTBPoE2gS4ZJaHR0cDovL2Ny
// SIG // bC5taWNyb3NvZnQuY29tL3BraS9jcmwvcHJvZHVjdHMv
// SIG // TWljUm9vQ2VyQXV0MjAxMV8yMDExXzAzXzIyLmNybDBe
// SIG // BggrBgEFBQcBAQRSMFAwTgYIKwYBBQUHMAKGQmh0dHA6
// SIG // Ly93d3cubWljcm9zb2Z0LmNvbS9wa2kvY2VydHMvTWlj
// SIG // Um9vQ2VyQXV0MjAxMV8yMDExXzAzXzIyLmNydDCBnwYD
// SIG // VR0gBIGXMIGUMIGRBgkrBgEEAYI3LgMwgYMwPwYIKwYB
// SIG // BQUHAgEWM2h0dHA6Ly93d3cubWljcm9zb2Z0LmNvbS9w
// SIG // a2lvcHMvZG9jcy9wcmltYXJ5Y3BzLmh0bTBABggrBgEF
// SIG // BQcCAjA0HjIgHQBMAGUAZwBhAGwAXwBwAG8AbABpAGMA
// SIG // eQBfAHMAdABhAHQAZQBtAGUAbgB0AC4gHTANBgkqhkiG
// SIG // 9w0BAQsFAAOCAgEAZ/KGpZjgVHkaLtPYdGcimwuWEeFj
// SIG // kplCln3SeQyQwWVfLiw++MNy0W2D/r4/6ArKO79HqaPz
// SIG // adtjvyI1pZddZYSQfYtGUFXYDJJ80hpLHPM8QotS0LD9
// SIG // a+M+By4pm+Y9G6XUtR13lDni6WTJRD14eiPzE32mkHSD
// SIG // jfTLJgJGKsKKELukqQUMm+1o+mgulaAqPyprWEljHwlp
// SIG // blqYluSD9MCP80Yr3vw70L01724lruWvJ+3Q3fMOr5ko
// SIG // l5hNDj0L8giJ1h/DMhji8MUtzluetEk5CsYKwsatruWy
// SIG // 2dsViFFFWDgycScaf7H0J/jeLDogaZiyWYlobm+nt3TD
// SIG // QAUGpgEqKD6CPxNNZgvAs0314Y9/HG8VfUWnduVAKmWj
// SIG // w11SYobDHWM2l4bf2vP48hahmifhzaWX0O5dY0HjWwec
// SIG // hz4GdwbRBrF1HxS+YWG18NzGGwS+30HHDiju3mUv7Jf2
// SIG // oVyW2ADWoUa9WfOXpQlLSBCZgB/QACnFsZulP0V3HjXG
// SIG // 0qKin3p6IvpIlR+r+0cjgPWe+L9rt0uX4ut1eBrs6jeZ
// SIG // eRhL/9azI2h15q/6/IvrC4DqaTuv/DDtBEyO3991bWOR
// SIG // PdGdVk5Pv4BXIqF4ETIheu9BCrE/+6jMpF3BoYibV3FW
// SIG // TkhFwELJm3ZbCoBIa/15n8G9bW1qyVJzEw16UM0xghqM
// SIG // MIIaiAIBATCBlTB+MQswCQYDVQQGEwJVUzETMBEGA1UE
// SIG // CBMKV2FzaGluZ3RvbjEQMA4GA1UEBxMHUmVkbW9uZDEe
// SIG // MBwGA1UEChMVTWljcm9zb2Z0IENvcnBvcmF0aW9uMSgw
// SIG // JgYDVQQDEx9NaWNyb3NvZnQgQ29kZSBTaWduaW5nIFBD
// SIG // QSAyMDExAhMzAAAEQBT8C+g+8SRfAAAAAARAMA0GCWCG
// SIG // SAFlAwQCAQUAoIGuMBkGCSqGSIb3DQEJAzEMBgorBgEE
// SIG // AYI3AgEEMBwGCisGAQQBgjcCAQsxDjAMBgorBgEEAYI3
// SIG // AgEVMC8GCSqGSIb3DQEJBDEiBCBsSIwmNiYubUkYliWe
// SIG // 3giSdqCDhryNMRQYNZByxCvJUTBCBgorBgEEAYI3AgEM
// SIG // MTQwMqAUgBIATQBpAGMAcgBvAHMAbwBmAHShGoAYaHR0
// SIG // cDovL3d3dy5taWNyb3NvZnQuY29tMA0GCSqGSIb3DQEB
// SIG // AQUABIIBgIFjRELct8snBa5C3JUore1MtrpBD2yG6860
// SIG // 9JejexNY2K9Ld0urL+KCuJDg17xmZ7ZE6CmKvLp19SGR
// SIG // b7eSh8oKSZETXmJDFo/3es9BqUmMCN6p4Ld/SJTBeMCN
// SIG // jdZoI/miweAqFMngoFuHxkS2tNiodeCGd8lZ+u+Q17sc
// SIG // JoLDXCa00PKQ4BqoLPIf7jSqTcBfh1HYiDlsPQRKk8iJ
// SIG // z6aAs13xG+5wKuqICwu67QdYZ7CDVs47ZgkwsEKlsnvp
// SIG // 5jDC0aP4skZ0n+K8RXzwGOjMApLyfufL/W8MDV36/+qZ
// SIG // zzhXOTF8UTaBYr9nAVgKyjvjGAVr42rSO+3WDXPA5sPl
// SIG // zu0Ac7fMGaPJsP6zxCs11R6z/DQeeVdYu9CgJ3dbwDZh
// SIG // V+YGGgUxNrGF9ycuuq7oxqgGRmnBgmcz4viPZffqDmDj
// SIG // NzPxJyhx1vo5mcjb+g9RKMJU2qHybx4uvPeQuewo32CO
// SIG // R7EmZwFCGqyZrA9jmOogRGIFb0FXNEagZPmFKKGCF5Yw
// SIG // gheSBgorBgEEAYI3AwMBMYIXgjCCF34GCSqGSIb3DQEH
// SIG // AqCCF28wghdrAgEDMQ8wDQYJYIZIAWUDBAIBBQAwggFR
// SIG // BgsqhkiG9w0BCRABBKCCAUAEggE8MIIBOAIBAQYKKwYB
// SIG // BAGEWQoDATAxMA0GCWCGSAFlAwQCAQUABCB3jYcZMkjr
// SIG // Ygl8126xyCQp2i4kAG/JCTcWBZh5Aa0wwgIGaMmDYd1x
// SIG // GBIyMDI1MTAwMzE3NDk0Ni45OVowBIACAfSggdGkgc4w
// SIG // gcsxCzAJBgNVBAYTAlVTMRMwEQYDVQQIEwpXYXNoaW5n
// SIG // dG9uMRAwDgYDVQQHEwdSZWRtb25kMR4wHAYDVQQKExVN
// SIG // aWNyb3NvZnQgQ29ycG9yYXRpb24xJTAjBgNVBAsTHE1p
// SIG // Y3Jvc29mdCBBbWVyaWNhIE9wZXJhdGlvbnMxJzAlBgNV
// SIG // BAsTHm5TaGllbGQgVFNTIEVTTjpBMDAwLTA1RTAtRDk0
// SIG // NzElMCMGA1UEAxMcTWljcm9zb2Z0IFRpbWUtU3RhbXAg
// SIG // U2VydmljZaCCEe0wggcgMIIFCKADAgECAhMzAAACCHid
// SIG // WF2Sx9lSAAEAAAIIMA0GCSqGSIb3DQEBCwUAMHwxCzAJ
// SIG // BgNVBAYTAlVTMRMwEQYDVQQIEwpXYXNoaW5ndG9uMRAw
// SIG // DgYDVQQHEwdSZWRtb25kMR4wHAYDVQQKExVNaWNyb3Nv
// SIG // ZnQgQ29ycG9yYXRpb24xJjAkBgNVBAMTHU1pY3Jvc29m
// SIG // dCBUaW1lLVN0YW1wIFBDQSAyMDEwMB4XDTI1MDEzMDE5
// SIG // NDI1M1oXDTI2MDQyMjE5NDI1M1owgcsxCzAJBgNVBAYT
// SIG // AlVTMRMwEQYDVQQIEwpXYXNoaW5ndG9uMRAwDgYDVQQH
// SIG // EwdSZWRtb25kMR4wHAYDVQQKExVNaWNyb3NvZnQgQ29y
// SIG // cG9yYXRpb24xJTAjBgNVBAsTHE1pY3Jvc29mdCBBbWVy
// SIG // aWNhIE9wZXJhdGlvbnMxJzAlBgNVBAsTHm5TaGllbGQg
// SIG // VFNTIEVTTjpBMDAwLTA1RTAtRDk0NzElMCMGA1UEAxMc
// SIG // TWljcm9zb2Z0IFRpbWUtU3RhbXAgU2VydmljZTCCAiIw
// SIG // DQYJKoZIhvcNAQEBBQADggIPADCCAgoCggIBALXLcAjm
// SIG // UjPcinWcrkExRRsZGyNKcLP9UazffO9jQ16yGw+B+V3f
// SIG // 9nf/d1LxYoEuUOWiyZck5mUVKI1dR3oNMnED2KT9lsJ1
// SIG // YnvwsqNs3e0WRfZzpFGlEykDyyr+gFtGvI/dzxD+DGkk
// SIG // AocfPxy5Kft7B8IvOc2bGqWJOTdDkserPY+N5goP91so
// SIG // wFPZMABYC+6bjP8dcgnq0V0ag1XhZRFmzAJK3pE7BqpD
// SIG // WgBga8Sd0f4NdmrX5seyC9w80J1NIilahtCIlL9QouJH
// SIG // TYo0KoHgj3JqMVNKWcwgQzP82LnfygYjrimFy82lR7b6
// SIG // popYdnx3hPUqCG9GZJIXhgXkM0QlvFoJTCzLudQuawWd
// SIG // No6NU6hMVZZ9Ze8G44qQFxApYYq+uSL3vqPjH7l7MA/f
// SIG // p+re7p0dElMtkC7h0S46ihTf6Qxmv5EFhaNMdAIpX7Jn
// SIG // VJPR4aRsdegDaXLJEOU2MFByh5kjFYJm2z93f6d/WOJI
// SIG // s3p/rB0dpTPQAPA5ND9oSjUgLzl4V4+/IgprEUmQZTYy
// SIG // prpfOreoKrm2iQge2OGiCysSB8MpN4VdO12GXUg+0twJ
// SIG // 8xxY4YYBeixVRTsb3jwXpb3rjbh/ZUcPwvcWjIAj36vj
// SIG // PIhBSaqIRLO5BZ5alNOMVjAKaBdoY65INXxw05VAHog/
// SIG // M+d5mFVOPDFsBmVpAgMBAAGjggFJMIIBRTAdBgNVHQ4E
// SIG // FgQUozHi986pxROBg5UH1/Xz+aF6AU0wHwYDVR0jBBgw
// SIG // FoAUn6cVXQBeYl2D9OXSZacbUzUZ6XIwXwYDVR0fBFgw
// SIG // VjBUoFKgUIZOaHR0cDovL3d3dy5taWNyb3NvZnQuY29t
// SIG // L3BraW9wcy9jcmwvTWljcm9zb2Z0JTIwVGltZS1TdGFt
// SIG // cCUyMFBDQSUyMDIwMTAoMSkuY3JsMGwGCCsGAQUFBwEB
// SIG // BGAwXjBcBggrBgEFBQcwAoZQaHR0cDovL3d3dy5taWNy
// SIG // b3NvZnQuY29tL3BraW9wcy9jZXJ0cy9NaWNyb3NvZnQl
// SIG // MjBUaW1lLVN0YW1wJTIwUENBJTIwMjAxMCgxKS5jcnQw
// SIG // DAYDVR0TAQH/BAIwADAWBgNVHSUBAf8EDDAKBggrBgEF
// SIG // BQcDCDAOBgNVHQ8BAf8EBAMCB4AwDQYJKoZIhvcNAQEL
// SIG // BQADggIBAFGzMokrAB+zCp1pA8WJpgH9k0BhYNrTjRdN
// SIG // xCwJoK5rmewbUiyVhpKkfuaJMuvp5ASpdzNmip45r/G8
// SIG // OcwaJ8Y11rQIdtDC2mciGy62so7aOGMRobCUmA4yqXbW
// SIG // vsiTpecHNrR7eEE67hQGQyX8sRf4BRG3uLv5FM2wY3Rx
// SIG // c/A9JtMUT73PtZqAZtj2nBSj83GQYmx6oYJD/0rZUxTv
// SIG // hvDl7v7wgZSEzbGykk+qdJ4c+FiZwHRZyU7FxUh+P9m5
// SIG // C/Cis9tMQRgNULNI3ftzTIKE8xjsUn4cYUE0nHB3mUoi
// SIG // vsZh+rxrSA6ILaWMZiVziu3hwJ53VcqDzd/SX1pRWKZY
// SIG // Fhe1815uGl+votzeMPw2CysOHO3RaCch2dNkKLuPGOwg
// SIG // GKUf32ljn+HptBwsor8TooI/0TVg3vx8to5eRczI7rEu
// SIG // u9Bn64JKLWF1O58ULuhIH8JTlFt8hUdcbSPWjafW2d7h
// SIG // 4Js18qpQ9MTfW01tYFHbdiLLSveRCYd5gTUYtsvinCSe
// SIG // pqKnUFGfpYhQwm2CdxrAQ3fd/wBgZnhrc2ceinMZVXqd
// SIG // 598ZVqDhN27L6jLVgX6yEKGhd0yp+E9YWkd7e4kZPgYk
// SIG // SI2zj7bxr/AdS4X5pFpHRw3k/teU7BTXfrSJQIm1B28p
// SIG // Bo0DAYjb0o7BLdAauJH0XaM4Y9QCWl4TMIIHcTCCBVmg
// SIG // AwIBAgITMwAAABXF52ueAptJmQAAAAAAFTANBgkqhkiG
// SIG // 9w0BAQsFADCBiDELMAkGA1UEBhMCVVMxEzARBgNVBAgT
// SIG // Cldhc2hpbmd0b24xEDAOBgNVBAcTB1JlZG1vbmQxHjAc
// SIG // BgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjEyMDAG
// SIG // A1UEAxMpTWljcm9zb2Z0IFJvb3QgQ2VydGlmaWNhdGUg
// SIG // QXV0aG9yaXR5IDIwMTAwHhcNMjEwOTMwMTgyMjI1WhcN
// SIG // MzAwOTMwMTgzMjI1WjB8MQswCQYDVQQGEwJVUzETMBEG
// SIG // A1UECBMKV2FzaGluZ3RvbjEQMA4GA1UEBxMHUmVkbW9u
// SIG // ZDEeMBwGA1UEChMVTWljcm9zb2Z0IENvcnBvcmF0aW9u
// SIG // MSYwJAYDVQQDEx1NaWNyb3NvZnQgVGltZS1TdGFtcCBQ
// SIG // Q0EgMjAxMDCCAiIwDQYJKoZIhvcNAQEBBQADggIPADCC
// SIG // AgoCggIBAOThpkzntHIhC3miy9ckeb0O1YLT/e6cBwfS
// SIG // qWxOdcjKNVf2AX9sSuDivbk+F2Az/1xPx2b3lVNxWuJ+
// SIG // Slr+uDZnhUYjDLWNE893MsAQGOhgfWpSg0S3po5GawcU
// SIG // 88V29YZQ3MFEyHFcUTE3oAo4bo3t1w/YJlN8OWECesSq
// SIG // /XJprx2rrPY2vjUmZNqYO7oaezOtgFt+jBAcnVL+tuhi
// SIG // JdxqD89d9P6OU8/W7IVWTe/dvI2k45GPsjksUZzpcGkN
// SIG // yjYtcI4xyDUoveO0hyTD4MmPfrVUj9z6BVWYbWg7mka9
// SIG // 7aSueik3rMvrg0XnRm7KMtXAhjBcTyziYrLNueKNiOSW
// SIG // rAFKu75xqRdbZ2De+JKRHh09/SDPc31BmkZ1zcRfNN0S
// SIG // idb9pSB9fvzZnkXftnIv231fgLrbqn427DZM9ituqBJR
// SIG // 6L8FA6PRc6ZNN3SUHDSCD/AQ8rdHGO2n6Jl8P0zbr17C
// SIG // 89XYcz1DTsEzOUyOArxCaC4Q6oRRRuLRvWoYWmEBc8pn
// SIG // ol7XKHYC4jMYctenIPDC+hIK12NvDMk2ZItboKaDIV1f
// SIG // MHSRlJTYuVD5C4lh8zYGNRiER9vcG9H9stQcxWv2XFJR
// SIG // XRLbJbqvUAV6bMURHXLvjflSxIUXk8A8FdsaN8cIFRg/
// SIG // eKtFtvUeh17aj54WcmnGrnu3tz5q4i6tAgMBAAGjggHd
// SIG // MIIB2TASBgkrBgEEAYI3FQEEBQIDAQABMCMGCSsGAQQB
// SIG // gjcVAgQWBBQqp1L+ZMSavoKRPEY1Kc8Q/y8E7jAdBgNV
// SIG // HQ4EFgQUn6cVXQBeYl2D9OXSZacbUzUZ6XIwXAYDVR0g
// SIG // BFUwUzBRBgwrBgEEAYI3TIN9AQEwQTA/BggrBgEFBQcC
// SIG // ARYzaHR0cDovL3d3dy5taWNyb3NvZnQuY29tL3BraW9w
// SIG // cy9Eb2NzL1JlcG9zaXRvcnkuaHRtMBMGA1UdJQQMMAoG
// SIG // CCsGAQUFBwMIMBkGCSsGAQQBgjcUAgQMHgoAUwB1AGIA
// SIG // QwBBMAsGA1UdDwQEAwIBhjAPBgNVHRMBAf8EBTADAQH/
// SIG // MB8GA1UdIwQYMBaAFNX2VsuP6KJcYmjRPZSQW9fOmhjE
// SIG // MFYGA1UdHwRPME0wS6BJoEeGRWh0dHA6Ly9jcmwubWlj
// SIG // cm9zb2Z0LmNvbS9wa2kvY3JsL3Byb2R1Y3RzL01pY1Jv
// SIG // b0NlckF1dF8yMDEwLTA2LTIzLmNybDBaBggrBgEFBQcB
// SIG // AQROMEwwSgYIKwYBBQUHMAKGPmh0dHA6Ly93d3cubWlj
// SIG // cm9zb2Z0LmNvbS9wa2kvY2VydHMvTWljUm9vQ2VyQXV0
// SIG // XzIwMTAtMDYtMjMuY3J0MA0GCSqGSIb3DQEBCwUAA4IC
// SIG // AQCdVX38Kq3hLB9nATEkW+Geckv8qW/qXBS2Pk5HZHix
// SIG // BpOXPTEztTnXwnE2P9pkbHzQdTltuw8x5MKP+2zRoZQY
// SIG // Iu7pZmc6U03dmLq2HnjYNi6cqYJWAAOwBb6J6Gngugnu
// SIG // e99qb74py27YP0h1AdkY3m2CDPVtI1TkeFN1JFe53Z/z
// SIG // jj3G82jfZfakVqr3lbYoVSfQJL1AoL8ZthISEV09J+BA
// SIG // ljis9/kpicO8F7BUhUKz/AyeixmJ5/ALaoHCgRlCGVJ1
// SIG // ijbCHcNhcy4sa3tuPywJeBTpkbKpW99Jo3QMvOyRgNI9
// SIG // 5ko+ZjtPu4b6MhrZlvSP9pEB9s7GdP32THJvEKt1MMU0
// SIG // sHrYUP4KWN1APMdUbZ1jdEgssU5HLcEUBHG/ZPkkvnNt
// SIG // yo4JvbMBV0lUZNlz138eW0QBjloZkWsNn6Qo3GcZKCS6
// SIG // OEuabvshVGtqRRFHqfG3rsjoiV5PndLQTHa1V1QJsWkB
// SIG // RH58oWFsc/4Ku+xBZj1p/cvBQUl+fpO+y/g75LcVv7TO
// SIG // PqUxUYS8vwLBgqJ7Fx0ViY1w/ue10CgaiQuPNtq6TPmb
// SIG // /wrpNPgkNWcr4A245oyZ1uEi6vAnQj0llOZ0dFtq0Z4+
// SIG // 7X6gMTN9vMvpe784cETRkPHIqzqKOghif9lwY1NNje6C
// SIG // baUFEMFxBmoQtB1VM1izoXBm8qGCA1AwggI4AgEBMIH5
// SIG // oYHRpIHOMIHLMQswCQYDVQQGEwJVUzETMBEGA1UECBMK
// SIG // V2FzaGluZ3RvbjEQMA4GA1UEBxMHUmVkbW9uZDEeMBwG
// SIG // A1UEChMVTWljcm9zb2Z0IENvcnBvcmF0aW9uMSUwIwYD
// SIG // VQQLExxNaWNyb3NvZnQgQW1lcmljYSBPcGVyYXRpb25z
// SIG // MScwJQYDVQQLEx5uU2hpZWxkIFRTUyBFU046QTAwMC0w
// SIG // NUUwLUQ5NDcxJTAjBgNVBAMTHE1pY3Jvc29mdCBUaW1l
// SIG // LVN0YW1wIFNlcnZpY2WiIwoBATAHBgUrDgMCGgMVAI2S
// SIG // +7Q0pxKN1grKuEllyzJc5RM0oIGDMIGApH4wfDELMAkG
// SIG // A1UEBhMCVVMxEzARBgNVBAgTCldhc2hpbmd0b24xEDAO
// SIG // BgNVBAcTB1JlZG1vbmQxHjAcBgNVBAoTFU1pY3Jvc29m
// SIG // dCBDb3Jwb3JhdGlvbjEmMCQGA1UEAxMdTWljcm9zb2Z0
// SIG // IFRpbWUtU3RhbXAgUENBIDIwMTAwDQYJKoZIhvcNAQEL
// SIG // BQACBQDsimjNMCIYDzIwMjUxMDAzMTUyMjUzWhgPMjAy
// SIG // NTEwMDQxNTIyNTNaMHcwPQYKKwYBBAGEWQoEATEvMC0w
// SIG // CgIFAOyKaM0CAQAwCgIBAAICMgoCAf8wBwIBAAICEu8w
// SIG // CgIFAOyLuk0CAQAwNgYKKwYBBAGEWQoEAjEoMCYwDAYK
// SIG // KwYBBAGEWQoDAqAKMAgCAQACAwehIKEKMAgCAQACAwGG
// SIG // oDANBgkqhkiG9w0BAQsFAAOCAQEAPYXopPR6nKc2FBLN
// SIG // qNOqL7eviSO1iljvpYCplAHdWFIer22gGBHyD0uy23YX
// SIG // SuuN5LnGZm4NYiBftVlwiVSGBlXsBD/I2khT/yowLqCW
// SIG // wnh0ljUOHfCJoW6Z6KI5DjlOcxUqtU+Aln1gv4tJfzR9
// SIG // hs4xq1xH/lqIjRTRnFKu6fszQvjgF+bLHouSu3Pmeibd
// SIG // U/CHh+X+XgThUOOpOEeaSiXmbQfX/O9Bt4sviDofMQA8
// SIG // BLvNSjXRao78FuoVyryTOfJXwB7CCK+u6RJURqQALW5c
// SIG // vedKgFPzBd6/mzF/do8Y4aOvJQkQLhWDmahtoxvCcSqY
// SIG // mOZGnCK3VTqVVKz5YjGCBA0wggQJAgEBMIGTMHwxCzAJ
// SIG // BgNVBAYTAlVTMRMwEQYDVQQIEwpXYXNoaW5ndG9uMRAw
// SIG // DgYDVQQHEwdSZWRtb25kMR4wHAYDVQQKExVNaWNyb3Nv
// SIG // ZnQgQ29ycG9yYXRpb24xJjAkBgNVBAMTHU1pY3Jvc29m
// SIG // dCBUaW1lLVN0YW1wIFBDQSAyMDEwAhMzAAACCHidWF2S
// SIG // x9lSAAEAAAIIMA0GCWCGSAFlAwQCAQUAoIIBSjAaBgkq
// SIG // hkiG9w0BCQMxDQYLKoZIhvcNAQkQAQQwLwYJKoZIhvcN
// SIG // AQkEMSIEILHQ8O4x9pXz7tlHTYwD58wlZv1eTkd65Yqj
// SIG // 6IwYraZcMIH6BgsqhkiG9w0BCRACLzGB6jCB5zCB5DCB
// SIG // vQQgj/+ObwkdrZbU73vvy334W3Znk2Yqq20+TpD71FGm
// SIG // Z6kwgZgwgYCkfjB8MQswCQYDVQQGEwJVUzETMBEGA1UE
// SIG // CBMKV2FzaGluZ3RvbjEQMA4GA1UEBxMHUmVkbW9uZDEe
// SIG // MBwGA1UEChMVTWljcm9zb2Z0IENvcnBvcmF0aW9uMSYw
// SIG // JAYDVQQDEx1NaWNyb3NvZnQgVGltZS1TdGFtcCBQQ0Eg
// SIG // MjAxMAITMwAAAgh4nVhdksfZUgABAAACCDAiBCD1SzEx
// SIG // O1YofCilNoEw/KxObu6IUaAi2PiutnWclEWXnzANBgkq
// SIG // hkiG9w0BAQsFAASCAgB5x9yF8C/VznX8dbqzew+CO8Ij
// SIG // ocMCHBof4lA2SVf8b0JWo+Rj2tzE2IZoMpJnlSCqmQoq
// SIG // 03AOKiPfXcBzBXN04gPMoS5mdxPlshAHJLhvt7Z788mR
// SIG // VpJmXBXJRY63U0hRGDjIXvypRNx1EgXwG8CgyOrkY/z9
// SIG // 0YCKBWZXJbEBkn80/Fpowm3feIAkconnCnEmXD+Pf/sG
// SIG // 1oPpASF1I53QdWKRGze/rOdOT7IHIRi/Saj6f5bmbRCg
// SIG // P/dGBxAjNfWhaXvRSKM5XK2pEsN382MCcKf1eIgROVMT
// SIG // a98f41rUhj4mtSrM6XzO7SkNw5p5R2x+K1joTlpofJR9
// SIG // OkVcj2rwV6Le7JwhLjOTbdrFRUkNyq7rdqATxb7wTrEk
// SIG // hqGejlnefX42M1nkMkO+JRezwZbI75CUKG0ZzAdbtCjg
// SIG // rp4UFruf3ITwWAkgUBXPEhpG262ddKKebZYOxlIjz27i
// SIG // ya5L0Hbz/7dhJy95k0brCRbbvHobgT6c7zFwMwzDZPFj
// SIG // ErGlSIF8oiyIVYFZHLkvELTYxk3pMs3Z6Cxyuy4SPRyy
// SIG // +7CINYqlXGcc2h7PyRL0yEACtyv52UkIpKggD6D7oeak
// SIG // aMn2Z6wTkjoV3M6pZM5K+nJF0VDxIgln9puhPAf4eUxC
// SIG // oDru8lN/ds7eGszgdVSgyFQSQkif5BiGDwN5/r5w6g==
// SIG // End signature block
