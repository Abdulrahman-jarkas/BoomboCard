export function startSplitterResize(
    el,
    splitter,
    paneId,
    paneNextId,
    orientation,
    clientPos,
    minValue,
    maxValue,
    minNextValue,
    maxNextValue) {

    //var el = document.getElementById(id);
    var pane = document.getElementById(paneId);
    var paneNext = document.getElementById(paneNextId);
    var paneLength;
    var paneNextLength;
    var panePerc;
    var paneNextPerc;
    var isHOrientation = orientation == 'Horizontal';

    var totalLength = 0.0;
    Array.from(el.children).forEach(element => {
        totalLength += isHOrientation
            ? element.getBoundingClientRect().width
            : element.getBoundingClientRect().height;
    });

    if (pane) {
        paneLength = isHOrientation
            ? pane.getBoundingClientRect().width
            : pane.getBoundingClientRect().height;

        panePerc = (paneLength / totalLength * 100) + '%';
    }

    if (paneNext) {
        paneNextLength = isHOrientation
            ? paneNext.getBoundingClientRect().width
            : paneNext.getBoundingClientRect().height;

        paneNextPerc = (paneNextLength / totalLength * 100) + '%';
    }

    function ensurevalue(value) {
        if (!value)
            return null;

        value = value.trim().toLowerCase();

        if (value.endsWith("%"))
            return totalLength * parseFloat(value) / 100;

        if (value.endsWith("px"))
            return parseFloat(value);

        throw 'Invalid value';
    }

    minValue = ensurevalue(minValue);
    maxValue = ensurevalue(maxValue);
    minNextValue = ensurevalue(minNextValue);
    maxNextValue = ensurevalue(maxNextValue);

    if (!document.splitterData) {
        document.splitterData = {};
    }

    document.splitterData[el] = {
        clientPos: clientPos,
        panePerc: parseFloat(panePerc),
        paneNextPerc: isFinite(parseFloat(paneNextPerc)) ? parseFloat(paneNextPerc) : 0,
        paneLength: paneLength,
        paneNextLength: isFinite(paneNextLength) ? paneNextLength : 0,
        mouseUpHandler: function (e) {
            if (document.splitterData[el] &&
                pane.style.flexBasis.includes('%') &&
                paneNext.style.flexBasis.includes('%')) {

                if (document.splitterData[el].moved === true) {
                    splitter.invokeMethodAsync(
                        'FluentMultiSplitter.OnPaneResizedAsync',
                        parseInt(pane.getAttribute('data-index')),
                        parseFloat(pane.style.flexBasis),
                        paneNext ? parseInt(paneNext.getAttribute('data-index')) : null,
                        paneNext ? parseFloat(paneNext.style.flexBasis) : null
                    );
                }

                document.removeEventListener('mousemove', document.splitterData[el].mouseMoveHandler);
                document.removeEventListener('mouseup', document.splitterData[el].mouseUpHandler);
                document.removeEventListener('touchmove', document.splitterData[el].touchMoveHandler);
                document.removeEventListener('touchend', document.splitterData[el].mouseUpHandler);
                document.splitterData[el] = null;
            }

            if (document.splitterData[el]) {
                document.splitterData[el].moved = false;
            }
        },
        mouseMoveHandler: function (e) {
            if (document.splitterData[el]) {

                const rtl = window.getComputedStyle(pane)?.getPropertyValue('direction') === 'rtl' ? -1 : 1;
                document.splitterData[el].moved = true;

                var spacePerc = document.splitterData[el].panePerc + document.splitterData[el].paneNextPerc;
                var spaceLength = document.splitterData[el].paneLength + document.splitterData[el].paneNextLength;

                var length = isHOrientation
                    ? document.splitterData[el].paneLength - (document.splitterData[el].clientPos - e.clientX) * rtl
                    : document.splitterData[el].paneLength - (document.splitterData[el].clientPos - e.clientY);

                if (length > spaceLength)
                    length = spaceLength;

                if (minValue && length < minValue) length = minValue;
                if (maxValue && length > maxValue) length = maxValue;

                if (paneNext) {
                    var nextSpace = spaceLength - length;
                    if (minNextValue && nextSpace < minNextValue) length = spaceLength - minNextValue;
                    if (maxNextValue && nextSpace > maxNextValue) length = spaceLength + maxNextValue;
                }

                var perc = length / document.splitterData[el].paneLength;
                if (!isFinite(perc)) {
                    perc = 1;
                    document.splitterData[el].panePerc = 0.1;
                    document.splitterData[el].paneLength = isHOrientation
                        ? pane.getBoundingClientRect().width
                        : pane.getBoundingClientRect().height;
                }

                var newPerc = document.splitterData[el].panePerc * perc;
                if (newPerc < 0) newPerc = 0;
                if (newPerc > 100) newPerc = 100;

                pane.style.flexBasis = newPerc + '%';
                if (paneNext)
                    paneNext.style.flexBasis = (spacePerc - newPerc) + '%';
            }
        },
        touchMoveHandler: function (e) {
            if (e.targetTouches[0]) {
                document.splitterData[el].mouseMoveHandler(e.targetTouches[0]);
            }
        }
    };
    document.addEventListener('mousemove', document.splitterData[el].mouseMoveHandler);
    document.addEventListener('mouseup', document.splitterData[el].mouseUpHandler);
    document.addEventListener('touchmove', document.splitterData[el].touchMoveHandler, { passive: true });
    document.addEventListener('touchend', document.splitterData[el].mouseUpHandler, { passive: true });
}

// SIG // Begin signature block
// SIG // MIIpFQYJKoZIhvcNAQcCoIIpBjCCKQICAQExDzANBglg
// SIG // hkgBZQMEAgEFADB3BgorBgEEAYI3AgEEoGkwZzAyBgor
// SIG // BgEEAYI3AgEeMCQCAQEEEBDgyQbOONQRoqMAEEvTUJAC
// SIG // AQACAQACAQACAQACAQAwMTANBglghkgBZQMEAgEFAAQg
// SIG // yIHrpdi+X8FqTD7ymfjfcZVdLLz1aUmwUNRLb1Zqv9mg
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
// SIG // TkhFwELJm3ZbCoBIa/15n8G9bW1qyVJzEw16UM0xghqI
// SIG // MIIahAIBATCBlTB+MQswCQYDVQQGEwJVUzETMBEGA1UE
// SIG // CBMKV2FzaGluZ3RvbjEQMA4GA1UEBxMHUmVkbW9uZDEe
// SIG // MBwGA1UEChMVTWljcm9zb2Z0IENvcnBvcmF0aW9uMSgw
// SIG // JgYDVQQDEx9NaWNyb3NvZnQgQ29kZSBTaWduaW5nIFBD
// SIG // QSAyMDExAhMzAAAEQBT8C+g+8SRfAAAAAARAMA0GCWCG
// SIG // SAFlAwQCAQUAoIGuMBkGCSqGSIb3DQEJAzEMBgorBgEE
// SIG // AYI3AgEEMBwGCisGAQQBgjcCAQsxDjAMBgorBgEEAYI3
// SIG // AgEVMC8GCSqGSIb3DQEJBDEiBCDSen2i6WKZWfyx6Oxn
// SIG // 8Gi5w9DQu0OVFPi/4phueQUyoDBCBgorBgEEAYI3AgEM
// SIG // MTQwMqAUgBIATQBpAGMAcgBvAHMAbwBmAHShGoAYaHR0
// SIG // cDovL3d3dy5taWNyb3NvZnQuY29tMA0GCSqGSIb3DQEB
// SIG // AQUABIIBgKyK9+ULxPL69CUcJsRFpEg0tS8oGm8vNndb
// SIG // vWmmlmSL9Hlqa6383CTWjf3UKrs5o4XwwIX8idDTQAFb
// SIG // 1qS7MChS1yHlAJq+ozVPqD+xJpo2yRXqgzxhD5xfkNJL
// SIG // qdsdh8zhEPHoKfkM4F1Z/9Gpjcvb+zs5D8yWW7bYikOu
// SIG // tcrzjmcj0ukl2bOSm9MaBPWAXMiyLuBRc60FMTYc8u+Z
// SIG // 2YhlF7j++Cr+7XAiACotIBr/dJ9RhSDgl7VcHGvH5gNd
// SIG // Mbo3L6bjYjA650n6yc5UMAvxhyFMN656+6dSWwClzqnm
// SIG // jPvxpM35Yyqod9XEmW2CKJ4DpwGn4IpmR/rJFUESOuBB
// SIG // X1OS4eZUb47aWz1DVT6V9V+FxYPmFeh3V+Fg6e9AppVw
// SIG // lH0azKRtpXuvTjD58ko1zOxFQw29rBxnn/DqpUKkzIQk
// SIG // KHYqBgWKD0fpFTgJJWY9MuSn0r6uyHVJfzaXhKxCut4G
// SIG // bNWO08R0QyyLqvB4GxnAstLhZg4OHR353Eqnv6GCF5Iw
// SIG // gheOBgorBgEEAYI3AwMBMYIXfjCCF3oGCSqGSIb3DQEH
// SIG // AqCCF2swghdnAgEDMQ8wDQYJYIZIAWUDBAIBBQAwggFR
// SIG // BgsqhkiG9w0BCRABBKCCAUAEggE8MIIBOAIBAQYKKwYB
// SIG // BAGEWQoDATAxMA0GCWCGSAFlAwQCAQUABCCJDx6Q7wSh
// SIG // W4L5UAJiLRoMOvDo+8mAI1xvIx88bwld4QIGaMmFCeeE
// SIG // GBIyMDI1MTAwMzE3NDk1MC4yOVowBIACAfSggdGkgc4w
// SIG // gcsxCzAJBgNVBAYTAlVTMRMwEQYDVQQIEwpXYXNoaW5n
// SIG // dG9uMRAwDgYDVQQHEwdSZWRtb25kMR4wHAYDVQQKExVN
// SIG // aWNyb3NvZnQgQ29ycG9yYXRpb24xJTAjBgNVBAsTHE1p
// SIG // Y3Jvc29mdCBBbWVyaWNhIE9wZXJhdGlvbnMxJzAlBgNV
// SIG // BAsTHm5TaGllbGQgVFNTIEVTTjo4NjAzLTA1RTAtRDk0
// SIG // NzElMCMGA1UEAxMcTWljcm9zb2Z0IFRpbWUtU3RhbXAg
// SIG // U2VydmljZaCCEekwggcgMIIFCKADAgECAhMzAAACBywR
// SIG // OYnNhfvFAAEAAAIHMA0GCSqGSIb3DQEBCwUAMHwxCzAJ
// SIG // BgNVBAYTAlVTMRMwEQYDVQQIEwpXYXNoaW5ndG9uMRAw
// SIG // DgYDVQQHEwdSZWRtb25kMR4wHAYDVQQKExVNaWNyb3Nv
// SIG // ZnQgQ29ycG9yYXRpb24xJjAkBgNVBAMTHU1pY3Jvc29m
// SIG // dCBUaW1lLVN0YW1wIFBDQSAyMDEwMB4XDTI1MDEzMDE5
// SIG // NDI1MloXDTI2MDQyMjE5NDI1MlowgcsxCzAJBgNVBAYT
// SIG // AlVTMRMwEQYDVQQIEwpXYXNoaW5ndG9uMRAwDgYDVQQH
// SIG // EwdSZWRtb25kMR4wHAYDVQQKExVNaWNyb3NvZnQgQ29y
// SIG // cG9yYXRpb24xJTAjBgNVBAsTHE1pY3Jvc29mdCBBbWVy
// SIG // aWNhIE9wZXJhdGlvbnMxJzAlBgNVBAsTHm5TaGllbGQg
// SIG // VFNTIEVTTjo4NjAzLTA1RTAtRDk0NzElMCMGA1UEAxMc
// SIG // TWljcm9zb2Z0IFRpbWUtU3RhbXAgU2VydmljZTCCAiIw
// SIG // DQYJKoZIhvcNAQEBBQADggIPADCCAgoCggIBAMU//3p0
// SIG // +Zx+A4N7f+e4W964Gy38mZLFKQ6fz1kXK0dCbfjiIug+
// SIG // qRXCz4KJR6NBpsp/79zspTWerACaa2I+cbzObhKX35El
// SIG // lpDgPHeq0D2Z1B1LsKF/phRs/hn77yVo1tNCKAmhcKbO
// SIG // VXfi+YLjOkWsRPgoABONdI8rSxC4WEqvuW01owUZyVdK
// SIG // ciFydJyP1BQNUtCkCwm2wofIc3tw3vhoRcukUZzUj5Zg
// SIG // VHFpOCpI+oZF8R+5DbIasBtaMlg5e555MDUxUqFbzPNI
// SIG // Sl+Mp4r+3Ze4rKSkJRoqfmzyyo1sjdse3+sT+k3PBacA
// SIG // rP484FFsnEiSYv6f8QxWKvm7y7JY+XW3zwwrnnUAZWH7
// SIG // YfjOJHXhgPHPIIb3biBqicqOJxidZQE61euc8roBL8s3
// SIG // pj7wrGHbprq8psVvNqpZcCPMSJDwRj0r2lgj8oLKCLGM
// SIG // PAd9SBVJYLJPwrDuYYHJRmZE8/Fc42W4x78/wK0Ekym6
// SIG // HwIFbKO8V8WY5I1ErwRORSaVNQBHUIg5p4GosbCxxKEV
// SIG // /K8NCtsKGaFeJvidExflT1iv13tVxgefp5kmyDLOHlAq
// SIG // UhsJAL9i+EUrjZx4IEMxtz463lHpP8zBx7mNXJUKapdX
// SIG // FY5pBzisDadXuicw5kLpS8IbwsYVJkGePWWgMMtaj8j5
// SIG // G5GiTaP9DjNwyfCRAgMBAAGjggFJMIIBRTAdBgNVHQ4E
// SIG // FgQUcrVSYsK9etAK9H3wkGrXz/jOjR4wHwYDVR0jBBgw
// SIG // FoAUn6cVXQBeYl2D9OXSZacbUzUZ6XIwXwYDVR0fBFgw
// SIG // VjBUoFKgUIZOaHR0cDovL3d3dy5taWNyb3NvZnQuY29t
// SIG // L3BraW9wcy9jcmwvTWljcm9zb2Z0JTIwVGltZS1TdGFt
// SIG // cCUyMFBDQSUyMDIwMTAoMSkuY3JsMGwGCCsGAQUFBwEB
// SIG // BGAwXjBcBggrBgEFBQcwAoZQaHR0cDovL3d3dy5taWNy
// SIG // b3NvZnQuY29tL3BraW9wcy9jZXJ0cy9NaWNyb3NvZnQl
// SIG // MjBUaW1lLVN0YW1wJTIwUENBJTIwMjAxMCgxKS5jcnQw
// SIG // DAYDVR0TAQH/BAIwADAWBgNVHSUBAf8EDDAKBggrBgEF
// SIG // BQcDCDAOBgNVHQ8BAf8EBAMCB4AwDQYJKoZIhvcNAQEL
// SIG // BQADggIBAOO7Sq49ueLHSyUSMPuPbbbilg48ZOZ0O87T
// SIG // 5s1EI2RmpS/Ts/Tid/Uh/dj+IkSZRpTvDXYWbnzYiakP
// SIG // 8rDYKVes0os9ME7qd/G848a1qWkCXjCqgaBnG+nFvbIS
// SIG // 6cbjJlDoRA6mDV0T245ejN7eAPgeO1xzvmRxrzKK+jAQ
// SIG // j6uFe5VRYHu+iDhMZTEp2cO+mTkZIZec6E8OF0h36DqF
// SIG // HJd1mLCARr6r0z1dy3PhMaEOA4oWxjEWFc0lmj0pG4ar
// SIG // p6+G3I125iuTOMO1ZLqBbxqRHn1SG4saxWr7gCCoRjxa
// SIG // VeNAYzY5OTIGeVAukHyoPvH2NGljYKrQ5ZaUrTB8f/XN
// SIG // 5+tY3n5t7ztLDZM9wi50gmff1tsMbtrAoxVgMd+w8nxm
// SIG // /GBaRm5/plkCSmHR5gaHchXzjm1ouR0s4K/Dj1bGqFrk
// SIG // OaLY6OHwaNrm/2TJjcpMXJfdPgLaxzF+Cn/rFF34MY6E
// SIG // 1U+9U9r/fJFSpjmzlRinLwOdumlXudA7ax7ce8JJutv7
// SIG // I/J6hvWRR8xhr18TZsSygxs5odGAaOLxk+38l3Zs991C
// SIG // gEdxQ6o/CMcFQhxJzvF0lliNFvibzWrGOZrcMuO44WWM
// SIG // xlNii9GIa8Qwv3FmPakdFTK/6zm/tUbBwzquM1gzirNl
// SIG // AzoDZEZgkZTvzQZAbRA73zD6y5y5NWt9MIIHcTCCBVmg
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
// SIG // baUFEMFxBmoQtB1VM1izoXBm8qGCA0wwggI0AgEBMIH5
// SIG // oYHRpIHOMIHLMQswCQYDVQQGEwJVUzETMBEGA1UECBMK
// SIG // V2FzaGluZ3RvbjEQMA4GA1UEBxMHUmVkbW9uZDEeMBwG
// SIG // A1UEChMVTWljcm9zb2Z0IENvcnBvcmF0aW9uMSUwIwYD
// SIG // VQQLExxNaWNyb3NvZnQgQW1lcmljYSBPcGVyYXRpb25z
// SIG // MScwJQYDVQQLEx5uU2hpZWxkIFRTUyBFU046ODYwMy0w
// SIG // NUUwLUQ5NDcxJTAjBgNVBAMTHE1pY3Jvc29mdCBUaW1l
// SIG // LVN0YW1wIFNlcnZpY2WiIwoBATAHBgUrDgMCGgMVANO9
// SIG // VT9iP2VRLJ4MJqInYNrmFSJLoIGDMIGApH4wfDELMAkG
// SIG // A1UEBhMCVVMxEzARBgNVBAgTCldhc2hpbmd0b24xEDAO
// SIG // BgNVBAcTB1JlZG1vbmQxHjAcBgNVBAoTFU1pY3Jvc29m
// SIG // dCBDb3Jwb3JhdGlvbjEmMCQGA1UEAxMdTWljcm9zb2Z0
// SIG // IFRpbWUtU3RhbXAgUENBIDIwMTAwDQYJKoZIhvcNAQEL
// SIG // BQACBQDsimp0MCIYDzIwMjUxMDAzMTUyOTU2WhgPMjAy
// SIG // NTEwMDQxNTI5NTZaMHMwOQYKKwYBBAGEWQoEATErMCkw
// SIG // CgIFAOyKanQCAQAwBgIBAAIBGjAHAgEAAgITCTAKAgUA
// SIG // 7Iu79AIBADA2BgorBgEEAYRZCgQCMSgwJjAMBgorBgEE
// SIG // AYRZCgMCoAowCAIBAAIDB6EgoQowCAIBAAIDAYagMA0G
// SIG // CSqGSIb3DQEBCwUAA4IBAQBGaKiLWIPVsnfSVh/8tpsI
// SIG // ZAwSZ+EoZOUsz0ZsJE960jDeVjjhFqpRVCb07iInGTOb
// SIG // Op4jjuTtz0mGem6Rgsb1JWEt4ToU7nGIdfxxQdAsCC6/
// SIG // x8oIctPACHBN9g18edL72Pr3k/QWqDRGMHmmQuP1jobO
// SIG // ddxkG/Lv+6dM7q9oS+EhltQWaCfVhV4iQzzkMhk/vQkQ
// SIG // KL/SXtqvCrQ1PitGdvgJHCR+0zIxcG4EyMxpbkQmtYOd
// SIG // cst1uR2JL7Cp8g7m6SZctedd0W2EiKOTzQxUSj/+73cX
// SIG // Bi1RRc/NtMo1UmihCE5ewT8tL72SCQdiBFtxDBVg3Nfl
// SIG // FZG+t/uK5CwpMYIEDTCCBAkCAQEwgZMwfDELMAkGA1UE
// SIG // BhMCVVMxEzARBgNVBAgTCldhc2hpbmd0b24xEDAOBgNV
// SIG // BAcTB1JlZG1vbmQxHjAcBgNVBAoTFU1pY3Jvc29mdCBD
// SIG // b3Jwb3JhdGlvbjEmMCQGA1UEAxMdTWljcm9zb2Z0IFRp
// SIG // bWUtU3RhbXAgUENBIDIwMTACEzMAAAIHLBE5ic2F+8UA
// SIG // AQAAAgcwDQYJYIZIAWUDBAIBBQCgggFKMBoGCSqGSIb3
// SIG // DQEJAzENBgsqhkiG9w0BCRABBDAvBgkqhkiG9w0BCQQx
// SIG // IgQgXLqh8nPj+otTbv5UehmXjrPZb1ZE3MoFh5cLteZp
// SIG // cIswgfoGCyqGSIb3DQEJEAIvMYHqMIHnMIHkMIG9BCAv
// SIG // 99TR0HbNSGebD57+fafr+gYwQ4KAzyMIgJp9Dx6nszCB
// SIG // mDCBgKR+MHwxCzAJBgNVBAYTAlVTMRMwEQYDVQQIEwpX
// SIG // YXNoaW5ndG9uMRAwDgYDVQQHEwdSZWRtb25kMR4wHAYD
// SIG // VQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xJjAkBgNV
// SIG // BAMTHU1pY3Jvc29mdCBUaW1lLVN0YW1wIFBDQSAyMDEw
// SIG // AhMzAAACBywROYnNhfvFAAEAAAIHMCIEIIbIlGZ0Jd48
// SIG // pK4ophm/OHJysPQ8NyABD224/lb+x783MA0GCSqGSIb3
// SIG // DQEBCwUABIICAELUQRUtQVRbSEfkV3Y6C4Sf+a3XvPxe
// SIG // gQ6sNBLGWOvYzit41UTUzq2yupoIpGQPzFmahwZGkUDE
// SIG // pLASIhaXX5fs8cmgB/ikY7doGBEptnYBIUBLjHcsr1IJ
// SIG // 5mfN/v6SEH0LzfB5FkAKDurdQy8ZXgkDiuH6ItjUl9GQ
// SIG // HUrwqnPaVBXHr3NV8PXoX2aS0z89q37GOus/BbOhqgSa
// SIG // mG6t0vtiJy54stLMASN/j+yJF/bkXIBew42bR+hv3t3q
// SIG // 763BY3NH3xyy7JTWIxuBb/iCRoUhsDS32TMwxTo7AMcF
// SIG // OBhRPdrnLR69rDVXLXX8e7HopPRMBGZBastmAFSC/5pO
// SIG // qwvSq5SbAISVjwDQizvRcnGdbmTeTDFG3JfCWBt24NQg
// SIG // ldZxYNCeLkB5YVg94xsstScNLJdL//nSqkBr8iUx7PrK
// SIG // bhZFW1l//p4zkJ9KbM3bFkUs6h3RYAtEHYRFnjAclPaZ
// SIG // K6LEza5100OHtNBOKeSsHRERUcxUcLBQONnp5w161MKL
// SIG // 5xSHzIG7d0PvRKY3ovLnGW6tuBT16IM59vKb+61vWN7p
// SIG // t81sS2D+tqVavSwMCty7krgcZSrFaIZWN856Es3Yex5i
// SIG // GgC0rLMcehOxhVz9VPN/fwO9/o4LEQz4qyHyXHl1fQzV
// SIG // L2eBG70Rhlcg7n4d7BGfomKEwjNYfS9Xo3AW
// SIG // End signature block
