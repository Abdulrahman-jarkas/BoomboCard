function GetMediaQueries() {
    return [
        { id: 'xs', items: document._fluentGrid.mediaXS, query: '(max-width: 599.98px)' },
        { id: 'sm', items: document._fluentGrid.mediaSM, query: '(min-width: 600px) and (max-width: 959.98px)' },
        { id: 'md', items: document._fluentGrid.mediaMD, query: '(min-width: 960px) and (max-width: 1279.98px)' },
        { id: 'lg', items: document._fluentGrid.mediaLG, query: '(min-width: 1280px) and (max-width: 1919.98px)' },
        { id: 'xl', items: document._fluentGrid.mediaXL, query: '(min-width: 1920px) and (max-width: 2559.98px)' },
        { id: 'xxl', items: document._fluentGrid.mediaXXL, query: '(min-width: 2560px)' },
    ];
}

export function FluentGridInitialize(id, dotNetHelper) {

    // Create a single instance of the media queries
    if (!document._fluentGrid) {
        document._fluentGrid = {
            mediaXS: [],
            mediaSM: [],
            mediaMD: [],
            mediaLG: [],
            mediaXL: [],
            mediaXXL: [],
        }

        // Add event listeners for each media query
        GetMediaQueries().forEach((mediaQuery) => {
            window.matchMedia(mediaQuery.query)
                  .addEventListener('change', media => {
                      if (media.matches) {
                          mediaQuery.items.forEach((item) => {
                              item.dotNetHelper.invokeMethodAsync('FluentGrid_MediaChangedAsync', mediaQuery.id);
                          });
                      }
                  });
        });
    }

    // Add the item to each media query
    document._fluentGrid.mediaXS.push({ id: id, dotNetHelper: dotNetHelper });
    document._fluentGrid.mediaSM.push({ id: id, dotNetHelper: dotNetHelper });
    document._fluentGrid.mediaMD.push({ id: id, dotNetHelper: dotNetHelper });
    document._fluentGrid.mediaLG.push({ id: id, dotNetHelper: dotNetHelper });
    document._fluentGrid.mediaXL.push({ id: id, dotNetHelper: dotNetHelper });
    document._fluentGrid.mediaXXL.push({ id: id, dotNetHelper: dotNetHelper });

    // First check
    GetMediaQueries().forEach((mediaQuery) => {
        if (window.matchMedia(mediaQuery.query).matches) {
            dotNetHelper.invokeMethodAsync('FluentGrid_MediaChangedAsync', mediaQuery.id);
        }
    });
}

export function FluentGridCleanup(id) {
    if (document._fluentGrid) {
        RemoveItem(document._fluentGrid.mediaXS, id);
        RemoveItem(document._fluentGrid.mediaSM, id);
        RemoveItem(document._fluentGrid.mediaMD, id);
        RemoveItem(document._fluentGrid.mediaLG, id);
        RemoveItem(document._fluentGrid.mediaXL, id);
        RemoveItem(document._fluentGrid.mediaXXL, id);
    }
}

// Remove the Array item where item.id is found
function RemoveItem(array, id) {
    for (var i = array.length - 1; i >= 0; i--) {
        if (array[i].id === id) {
            array.splice(i, 1);
        }
    }
}

// SIG // Begin signature block
// SIG // MIIpGgYJKoZIhvcNAQcCoIIpCzCCKQcCAQExDzANBglg
// SIG // hkgBZQMEAgEFADB3BgorBgEEAYI3AgEEoGkwZzAyBgor
// SIG // BgEEAYI3AgEeMCQCAQEEEBDgyQbOONQRoqMAEEvTUJAC
// SIG // AQACAQACAQACAQACAQAwMTANBglghkgBZQMEAgEFAAQg
// SIG // togfSsofYQfAPiV/kkJXNiRIFKN7bZ+rmAHsm/E0N+eg
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
// SIG // TkhFwELJm3ZbCoBIa/15n8G9bW1qyVJzEw16UM0xghqN
// SIG // MIIaiQIBATCBlTB+MQswCQYDVQQGEwJVUzETMBEGA1UE
// SIG // CBMKV2FzaGluZ3RvbjEQMA4GA1UEBxMHUmVkbW9uZDEe
// SIG // MBwGA1UEChMVTWljcm9zb2Z0IENvcnBvcmF0aW9uMSgw
// SIG // JgYDVQQDEx9NaWNyb3NvZnQgQ29kZSBTaWduaW5nIFBD
// SIG // QSAyMDExAhMzAAAEQBT8C+g+8SRfAAAAAARAMA0GCWCG
// SIG // SAFlAwQCAQUAoIGuMBkGCSqGSIb3DQEJAzEMBgorBgEE
// SIG // AYI3AgEEMBwGCisGAQQBgjcCAQsxDjAMBgorBgEEAYI3
// SIG // AgEVMC8GCSqGSIb3DQEJBDEiBCCrQtEIg3W0JYHmbk+I
// SIG // C8cCeEgnuLJCFWBKURPr34O3NTBCBgorBgEEAYI3AgEM
// SIG // MTQwMqAUgBIATQBpAGMAcgBvAHMAbwBmAHShGoAYaHR0
// SIG // cDovL3d3dy5taWNyb3NvZnQuY29tMA0GCSqGSIb3DQEB
// SIG // AQUABIIBgH5QJyRQtXqENPBTVpyR/hW8kyuAR2tb6X9t
// SIG // P2/NZYq+aUqJ14mGnrzWerIhN6VIXGDcBzm/ziwHx/HM
// SIG // 9+mHmKGZPuwiSz9of+pmdaweIW5Q1BNS/QipbJaPj+wv
// SIG // Cfn4qv6+Ko0ZMCSFo6myX9Rx/p9Po0xxvKgsiwI1Kw66
// SIG // 3rzTYlbOAje8xzhyZujn+DLFXfgFld6iuCeB9jJ03HOF
// SIG // p+ED3HS+QRORSNRWbSwKn5MfqU0AsmmuIgK3bGKWzNbg
// SIG // REsU2VhIF/d7T3VL+ZXDrUn/DOKmePvz6H0SDorykse6
// SIG // 4xDHJ4axMi+MhUzNgdjQ9bfxKwIMJbbL4HZRNtJbEYX7
// SIG // FMXqudZbTi1Z84XWKRYNoiS4NPEnx2J/YHgaYCMnvzqt
// SIG // olhichsngTlIePhXcq5wUxsj+dYD7W8QeAz0NnKGYBt7
// SIG // 7LB6bFX4RVCQLAeyedP73Bkh4B7hoTN5nfnEVY1MGeIi
// SIG // GIdWBR1QN8X+aGMOpM8MQhDFc46UZ0yv9s48sKGCF5cw
// SIG // gheTBgorBgEEAYI3AwMBMYIXgzCCF38GCSqGSIb3DQEH
// SIG // AqCCF3AwghdsAgEDMQ8wDQYJYIZIAWUDBAIBBQAwggFS
// SIG // BgsqhkiG9w0BCRABBKCCAUEEggE9MIIBOQIBAQYKKwYB
// SIG // BAGEWQoDATAxMA0GCWCGSAFlAwQCAQUABCDBC+8lXTPE
// SIG // AFCrD2kNMk73SWEzRax1196PMbm0jovnMwIGaKOmFtzB
// SIG // GBMyMDI1MTAwMzE3NTEzMS4wOTFaMASAAgH0oIHRpIHO
// SIG // MIHLMQswCQYDVQQGEwJVUzETMBEGA1UECBMKV2FzaGlu
// SIG // Z3RvbjEQMA4GA1UEBxMHUmVkbW9uZDEeMBwGA1UEChMV
// SIG // TWljcm9zb2Z0IENvcnBvcmF0aW9uMSUwIwYDVQQLExxN
// SIG // aWNyb3NvZnQgQW1lcmljYSBPcGVyYXRpb25zMScwJQYD
// SIG // VQQLEx5uU2hpZWxkIFRTUyBFU046ODkwMC0wNUUwLUQ5
// SIG // NDcxJTAjBgNVBAMTHE1pY3Jvc29mdCBUaW1lLVN0YW1w
// SIG // IFNlcnZpY2WgghHtMIIHIDCCBQigAwIBAgITMwAAAg4s
// SIG // yyh9lSB1YwABAAACDjANBgkqhkiG9w0BAQsFADB8MQsw
// SIG // CQYDVQQGEwJVUzETMBEGA1UECBMKV2FzaGluZ3RvbjEQ
// SIG // MA4GA1UEBxMHUmVkbW9uZDEeMBwGA1UEChMVTWljcm9z
// SIG // b2Z0IENvcnBvcmF0aW9uMSYwJAYDVQQDEx1NaWNyb3Nv
// SIG // ZnQgVGltZS1TdGFtcCBQQ0EgMjAxMDAeFw0yNTAxMzAx
// SIG // OTQzMDNaFw0yNjA0MjIxOTQzMDNaMIHLMQswCQYDVQQG
// SIG // EwJVUzETMBEGA1UECBMKV2FzaGluZ3RvbjEQMA4GA1UE
// SIG // BxMHUmVkbW9uZDEeMBwGA1UEChMVTWljcm9zb2Z0IENv
// SIG // cnBvcmF0aW9uMSUwIwYDVQQLExxNaWNyb3NvZnQgQW1l
// SIG // cmljYSBPcGVyYXRpb25zMScwJQYDVQQLEx5uU2hpZWxk
// SIG // IFRTUyBFU046ODkwMC0wNUUwLUQ5NDcxJTAjBgNVBAMT
// SIG // HE1pY3Jvc29mdCBUaW1lLVN0YW1wIFNlcnZpY2UwggIi
// SIG // MA0GCSqGSIb3DQEBAQUAA4ICDwAwggIKAoICAQCs5t7i
// SIG // RtXt0hbeo9ME78ZYjIo3saQuWMBFQ7X4s9vooYRABTOf
// SIG // 2poTHatx+EwnBUGB1V2t/E6MwsQNmY5XpM/75aCrZdxA
// SIG // nrV9o4Tu5sBepbbfehsrOWRBIGoJE6PtWod1CrFehm1d
// SIG // iz3jY3H8iFrh7nqefniZ1SnbcWPMyNIxuGFzpQiDA+E5
// SIG // YS33meMqaXwhdb01Cluymh/3EKvknj4dIpQZEWOPM3jx
// SIG // bRVAYN5J2tOrYkJcdDx0l02V/NYd1qkvUBgPxrKviq5k
// SIG // z7E6AbOifCDSMBgcn/X7RQw630Qkzqhp0kDU2qei/ao9
// SIG // IHmuuReXEjnjpgTsr4Ab33ICAKMYxOQe+n5wqEVcE9OT
// SIG // yhmWZJS5AnWUTniok4mgwONBWQ1DLOGFkZwXT334IPCq
// SIG // d4/3/Ld/ItizistyUZYsml/C4ZhdALbvfYwzv31Oxf8N
// SIG // TmV5IGxWdHnk2Hhh4bnzTKosEaDrJvQMiQ+loojM7f5b
// SIG // gdyBBnYQBm5+/iJsxw8k227zF2jbNI+Ows8HLeZGt8t6
// SIG // uJ2eVjND1B0YtgsBP0csBlnnI+4+dvLYRt0cAqw6PiYS
// SIG // z5FSZcbpi0xdAH/jd3dzyGArbyLuo69HugfGEEb/sM07
// SIG // rcoP1o3cZ8eWMb4+MIB8euOb5DVPDnEcFi4NDukYM91g
// SIG // 1Dt/qIek+rtE88VS8QIDAQABo4IBSTCCAUUwHQYDVR0O
// SIG // BBYEFIVxRGlSEZE+1ESK6UGI7YNcEIjbMB8GA1UdIwQY
// SIG // MBaAFJ+nFV0AXmJdg/Tl0mWnG1M1GelyMF8GA1UdHwRY
// SIG // MFYwVKBSoFCGTmh0dHA6Ly93d3cubWljcm9zb2Z0LmNv
// SIG // bS9wa2lvcHMvY3JsL01pY3Jvc29mdCUyMFRpbWUtU3Rh
// SIG // bXAlMjBQQ0ElMjAyMDEwKDEpLmNybDBsBggrBgEFBQcB
// SIG // AQRgMF4wXAYIKwYBBQUHMAKGUGh0dHA6Ly93d3cubWlj
// SIG // cm9zb2Z0LmNvbS9wa2lvcHMvY2VydHMvTWljcm9zb2Z0
// SIG // JTIwVGltZS1TdGFtcCUyMFBDQSUyMDIwMTAoMSkuY3J0
// SIG // MAwGA1UdEwEB/wQCMAAwFgYDVR0lAQH/BAwwCgYIKwYB
// SIG // BQUHAwgwDgYDVR0PAQH/BAQDAgeAMA0GCSqGSIb3DQEB
// SIG // CwUAA4ICAQB14L2TL+L8OXLxnGSal2h30mZ7FsBFooiY
// SIG // kUVOY05F9pnwPTVufEDGWEpNNy2OfaUHWIOoQ/9/rjwO
// SIG // 0hS2SpB0BzMAk2gyz92NGWOpWbpBdMvrrRDpiWZi/uLS
// SIG // 4ZGdRn3P2DccYmlkNP+vaRAXvnv+mp27KgI79mJ9hGyC
// SIG // QbvtMIjkbYoLqK7sF7Wahn9rLjX1y5QJL4lvEy3QmA9K
// SIG // RBj56cEv/lAvzDq7eSiqRq/pCyqyc8uzmQ8SeKWyWu6D
// SIG // jUA9vi84QsmLjqPGCnH4cPyg+t95RpW+73snhew1iCV+
// SIG // wXu2RxMnWg7EsD5eLkJHLszUIPd+XClD+FTvV03GfrDD
// SIG // fk+45flH/eKRZc3MUZtnhLJjPwv3KoKDScW4iV6SbCRy
// SIG // cYPkqoWBrHf7SvDA7GrH2UOtz1Wa1k27sdZgpG6/c9Cq
// SIG // KI8CX5vgaa+A7oYHb4ZBj7S8u8sgxwWK7HgWDRByOH3C
// SIG // iJu4LJ8h3TiRkRArmHRp0lbNf1iAKuL886IKE912v0yq
// SIG // 55t8jMxjBU7uoLsrYVIoKkzh+sAkgkpGOoZL14+dlxVM
// SIG // 91Bavza4kODTUlwzb+SpXsSqVx8nuB6qhUy7pqpgww1q
// SIG // 4SNhAxFnFxsxiTlaoL75GNxPR605lJ2WXehtEi7/+YfJ
// SIG // qvH+vnqcpqCjyQ9hNaVzuOEHX4MyuqcjwjCCB3EwggVZ
// SIG // oAMCAQICEzMAAAAVxedrngKbSZkAAAAAABUwDQYJKoZI
// SIG // hvcNAQELBQAwgYgxCzAJBgNVBAYTAlVTMRMwEQYDVQQI
// SIG // EwpXYXNoaW5ndG9uMRAwDgYDVQQHEwdSZWRtb25kMR4w
// SIG // HAYDVQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xMjAw
// SIG // BgNVBAMTKU1pY3Jvc29mdCBSb290IENlcnRpZmljYXRl
// SIG // IEF1dGhvcml0eSAyMDEwMB4XDTIxMDkzMDE4MjIyNVoX
// SIG // DTMwMDkzMDE4MzIyNVowfDELMAkGA1UEBhMCVVMxEzAR
// SIG // BgNVBAgTCldhc2hpbmd0b24xEDAOBgNVBAcTB1JlZG1v
// SIG // bmQxHjAcBgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlv
// SIG // bjEmMCQGA1UEAxMdTWljcm9zb2Z0IFRpbWUtU3RhbXAg
// SIG // UENBIDIwMTAwggIiMA0GCSqGSIb3DQEBAQUAA4ICDwAw
// SIG // ggIKAoICAQDk4aZM57RyIQt5osvXJHm9DtWC0/3unAcH
// SIG // 0qlsTnXIyjVX9gF/bErg4r25PhdgM/9cT8dm95VTcVri
// SIG // fkpa/rg2Z4VGIwy1jRPPdzLAEBjoYH1qUoNEt6aORmsH
// SIG // FPPFdvWGUNzBRMhxXFExN6AKOG6N7dcP2CZTfDlhAnrE
// SIG // qv1yaa8dq6z2Nr41JmTamDu6GnszrYBbfowQHJ1S/rbo
// SIG // YiXcag/PXfT+jlPP1uyFVk3v3byNpOORj7I5LFGc6XBp
// SIG // Dco2LXCOMcg1KL3jtIckw+DJj361VI/c+gVVmG1oO5pG
// SIG // ve2krnopN6zL64NF50ZuyjLVwIYwXE8s4mKyzbnijYjk
// SIG // lqwBSru+cakXW2dg3viSkR4dPf0gz3N9QZpGdc3EXzTd
// SIG // EonW/aUgfX782Z5F37ZyL9t9X4C626p+Nuw2TPYrbqgS
// SIG // Uei/BQOj0XOmTTd0lBw0gg/wEPK3Rxjtp+iZfD9M269e
// SIG // wvPV2HM9Q07BMzlMjgK8QmguEOqEUUbi0b1qGFphAXPK
// SIG // Z6Je1yh2AuIzGHLXpyDwwvoSCtdjbwzJNmSLW6CmgyFd
// SIG // XzB0kZSU2LlQ+QuJYfM2BjUYhEfb3BvR/bLUHMVr9lxS
// SIG // UV0S2yW6r1AFemzFER1y7435UsSFF5PAPBXbGjfHCBUY
// SIG // P3irRbb1Hode2o+eFnJpxq57t7c+auIurQIDAQABo4IB
// SIG // 3TCCAdkwEgYJKwYBBAGCNxUBBAUCAwEAATAjBgkrBgEE
// SIG // AYI3FQIEFgQUKqdS/mTEmr6CkTxGNSnPEP8vBO4wHQYD
// SIG // VR0OBBYEFJ+nFV0AXmJdg/Tl0mWnG1M1GelyMFwGA1Ud
// SIG // IARVMFMwUQYMKwYBBAGCN0yDfQEBMEEwPwYIKwYBBQUH
// SIG // AgEWM2h0dHA6Ly93d3cubWljcm9zb2Z0LmNvbS9wa2lv
// SIG // cHMvRG9jcy9SZXBvc2l0b3J5Lmh0bTATBgNVHSUEDDAK
// SIG // BggrBgEFBQcDCDAZBgkrBgEEAYI3FAIEDB4KAFMAdQBi
// SIG // AEMAQTALBgNVHQ8EBAMCAYYwDwYDVR0TAQH/BAUwAwEB
// SIG // /zAfBgNVHSMEGDAWgBTV9lbLj+iiXGJo0T2UkFvXzpoY
// SIG // xDBWBgNVHR8ETzBNMEugSaBHhkVodHRwOi8vY3JsLm1p
// SIG // Y3Jvc29mdC5jb20vcGtpL2NybC9wcm9kdWN0cy9NaWNS
// SIG // b29DZXJBdXRfMjAxMC0wNi0yMy5jcmwwWgYIKwYBBQUH
// SIG // AQEETjBMMEoGCCsGAQUFBzAChj5odHRwOi8vd3d3Lm1p
// SIG // Y3Jvc29mdC5jb20vcGtpL2NlcnRzL01pY1Jvb0NlckF1
// SIG // dF8yMDEwLTA2LTIzLmNydDANBgkqhkiG9w0BAQsFAAOC
// SIG // AgEAnVV9/Cqt4SwfZwExJFvhnnJL/Klv6lwUtj5OR2R4
// SIG // sQaTlz0xM7U518JxNj/aZGx80HU5bbsPMeTCj/ts0aGU
// SIG // GCLu6WZnOlNN3Zi6th542DYunKmCVgADsAW+iehp4LoJ
// SIG // 7nvfam++Kctu2D9IdQHZGN5tggz1bSNU5HhTdSRXud2f
// SIG // 8449xvNo32X2pFaq95W2KFUn0CS9QKC/GbYSEhFdPSfg
// SIG // QJY4rPf5KYnDvBewVIVCs/wMnosZiefwC2qBwoEZQhlS
// SIG // dYo2wh3DYXMuLGt7bj8sCXgU6ZGyqVvfSaN0DLzskYDS
// SIG // PeZKPmY7T7uG+jIa2Zb0j/aRAfbOxnT99kxybxCrdTDF
// SIG // NLB62FD+CljdQDzHVG2dY3RILLFORy3BFARxv2T5JL5z
// SIG // bcqOCb2zAVdJVGTZc9d/HltEAY5aGZFrDZ+kKNxnGSgk
// SIG // ujhLmm77IVRrakURR6nxt67I6IleT53S0Ex2tVdUCbFp
// SIG // AUR+fKFhbHP+CrvsQWY9af3LwUFJfn6Tvsv4O+S3Fb+0
// SIG // zj6lMVGEvL8CwYKiexcdFYmNcP7ntdAoGokLjzbaukz5
// SIG // m/8K6TT4JDVnK+ANuOaMmdbhIurwJ0I9JZTmdHRbatGe
// SIG // Pu1+oDEzfbzL6Xu/OHBE0ZDxyKs6ijoIYn/ZcGNTTY3u
// SIG // gm2lBRDBcQZqELQdVTNYs6FwZvKhggNQMIICOAIBATCB
// SIG // +aGB0aSBzjCByzELMAkGA1UEBhMCVVMxEzARBgNVBAgT
// SIG // Cldhc2hpbmd0b24xEDAOBgNVBAcTB1JlZG1vbmQxHjAc
// SIG // BgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjElMCMG
// SIG // A1UECxMcTWljcm9zb2Z0IEFtZXJpY2EgT3BlcmF0aW9u
// SIG // czEnMCUGA1UECxMeblNoaWVsZCBUU1MgRVNOOjg5MDAt
// SIG // MDVFMC1EOTQ3MSUwIwYDVQQDExxNaWNyb3NvZnQgVGlt
// SIG // ZS1TdGFtcCBTZXJ2aWNloiMKAQEwBwYFKw4DAhoDFQBK
// SIG // 6HY/ZWLnOcMEQsjkDAoB/JZWCKCBgzCBgKR+MHwxCzAJ
// SIG // BgNVBAYTAlVTMRMwEQYDVQQIEwpXYXNoaW5ndG9uMRAw
// SIG // DgYDVQQHEwdSZWRtb25kMR4wHAYDVQQKExVNaWNyb3Nv
// SIG // ZnQgQ29ycG9yYXRpb24xJjAkBgNVBAMTHU1pY3Jvc29m
// SIG // dCBUaW1lLVN0YW1wIFBDQSAyMDEwMA0GCSqGSIb3DQEB
// SIG // CwUAAgUA7IobRjAiGA8yMDI1MTAwMzA5NTIwNloYDzIw
// SIG // MjUxMDA0MDk1MjA2WjB3MD0GCisGAQQBhFkKBAExLzAt
// SIG // MAoCBQDsihtGAgEAMAoCAQACAjWyAgH/MAcCAQACAhLQ
// SIG // MAoCBQDsi2zGAgEAMDYGCisGAQQBhFkKBAIxKDAmMAwG
// SIG // CisGAQQBhFkKAwKgCjAIAgEAAgMHoSChCjAIAgEAAgMB
// SIG // hqAwDQYJKoZIhvcNAQELBQADggEBAOuwxTXzg3Af8cgr
// SIG // 6O+gcxUFnUByn1a+SRZ6xlkUszrFAVh4OibpOGN12Ask
// SIG // FUfF5w2tvXGToYOzk9dz0inVv1Vtar9aNAFvg/QgcccL
// SIG // afA29Gq4+Ibqc+TTd4S/cj7d5bNvo4RhnPJA3Nl4oLQA
// SIG // 2jyMA3fKrEsDaZy38r5P95ZsDmQF8QFvm81rzsdiL7Q6
// SIG // C7ivkFbPWDkzxxW9Sizi+Eo8vm2ATIdQLQS+GL/D9M40
// SIG // koPlPNRS71SNoQgZIXs9va9QGVYBiEF1hNa5Fcay9A/v
// SIG // uzlssD7l0YhyUl4pF25S3jq774F4Uqnd/5oYhv4l9m12
// SIG // BItq1nTsyUTFiZS7OWcxggQNMIIECQIBATCBkzB8MQsw
// SIG // CQYDVQQGEwJVUzETMBEGA1UECBMKV2FzaGluZ3RvbjEQ
// SIG // MA4GA1UEBxMHUmVkbW9uZDEeMBwGA1UEChMVTWljcm9z
// SIG // b2Z0IENvcnBvcmF0aW9uMSYwJAYDVQQDEx1NaWNyb3Nv
// SIG // ZnQgVGltZS1TdGFtcCBQQ0EgMjAxMAITMwAAAg4syyh9
// SIG // lSB1YwABAAACDjANBglghkgBZQMEAgEFAKCCAUowGgYJ
// SIG // KoZIhvcNAQkDMQ0GCyqGSIb3DQEJEAEEMC8GCSqGSIb3
// SIG // DQEJBDEiBCClMD+k7oKpX0NyDL6/18k/IFiUvYE87DB0
// SIG // saYt1sL6ojCB+gYLKoZIhvcNAQkQAi8xgeowgecwgeQw
// SIG // gb0EIAF0HXMl8OmBkK267mxobKSihwOdP0eUNXQMypPz
// SIG // TxKGMIGYMIGApH4wfDELMAkGA1UEBhMCVVMxEzARBgNV
// SIG // BAgTCldhc2hpbmd0b24xEDAOBgNVBAcTB1JlZG1vbmQx
// SIG // HjAcBgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjEm
// SIG // MCQGA1UEAxMdTWljcm9zb2Z0IFRpbWUtU3RhbXAgUENB
// SIG // IDIwMTACEzMAAAIOLMsofZUgdWMAAQAAAg4wIgQgq9a8
// SIG // 1PDelTSue0M2way0xyNtJt/ygFszJm9fy8X9v9wwDQYJ
// SIG // KoZIhvcNAQELBQAEggIAL4pIZV6lVU+KNOVpPtd6q2AM
// SIG // izwqSxllQBMGOf+vcD9I3AQAaEgUNVsKto33Nb84ZgnD
// SIG // 5bh1sABMTuRhaHX9/ZPD6veaamJv08a4SlO/KsutzTsA
// SIG // OiUhL+4cmOI4Xe3mxnPgZxmCrLG8nDqNRT0z0UMvUmMW
// SIG // WMzyox0+Q35+Ufryjv4mr4O2YqgwB8E89KFtJq3MzTWg
// SIG // Sf+CfS4/+7seO9B3Dyrv1tstDH+kZ24OCaOnbDMhh5DA
// SIG // M1sS1X0YX7iRT48geUYi++Wv3GnDZcloBWd2Ze2uo9V1
// SIG // Ses6oqUqanoLwLIAnCWwIttFBECvhYhamVg/R/JHJRTC
// SIG // toAFqVUokIbjmDicCzjO86mMi+wS5E4G0AHoV8RCfWtD
// SIG // cySfKv28GGeyBkVcG4upssmbyzGDYNYbxXuKY+9iwi0x
// SIG // 0rFtpYPbWcn80na5SUwm2AzyJoGe5e1XAmnOFqsrfd0b
// SIG // G7tdn9eugjGeDhNQhwSnWg88/ZFA03u/uqpJDMSeN1Z5
// SIG // FeymyK9fYRKRAjTbN2qmvzENsJ9oaAy0uoVhmYqnkUYu
// SIG // pDynLiTgWTObxRGvwoOTNqRXBOw75iTxaxq5vAEQUO1d
// SIG // PKnjhhgkOt3e18DONeMP3vTPY5O2G/c0wDtYjTSVua3b
// SIG // 7xzLPRa86i+MmqgqAxLMPfT3maCcUu9dCkIYA5/KJeI=
// SIG // End signature block
