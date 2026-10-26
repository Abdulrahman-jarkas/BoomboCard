export function onLoad() {
    const mql = window.matchMedia("(max-width: 600px)");

    for (let expander of document.getElementsByClassName("expander")) {
        if (expander) {
            const origStyle = expander.parentElement.style.cssText;
            expander.addEventListener('click', (ev) => toggleMenuExpandedAsync(expander, origStyle, ev));
            expander.addEventListener('keydown', (ev) => handleMenuExpanderKeyDownAsync(expander, origStyle, ev));

            mql.onchange = (e) => {
                if (e.matches) {
                    setMenuExpanded(expander, origStyle, true)
                }
            };
        }
    }
    for (let element of document.getElementsByClassName("fluent-nav-group")) {
        attachEventHandlers(element);
    }
}
export function onUpdate() {
    
}

export function onDispose() {
    for (let expander of document.getElementsByClassName("expander")) {
        if (expander) {
            expander.removeEventListener('click', toggleMenuExpandedAsync);
            expander.removeEventListener('keydown', handleMenuExpanderKeyDownAsync);
        }
    }
    for (let element of document.getElementsByClassName("fluent-nav-group")) {
        detachEventHandlers(element);
    }
}

function attachEventHandlers(element) {
    let navlink = element.getElementsByClassName("fluent-nav-link")[0];
    if (!navlink) {
        return;
    }
    if (!navlink.href) {
        navlink.addEventListener('click', () => toggleGroupExpandedAsync(element));
    }
    navlink.addEventListener('keydown', (ev) => handleExpanderKeyDownAsync(element, ev));

    let expandCollapseButton = element.getElementsByClassName("expand-collapse-button")[0];
    if (!expandCollapseButton) {
        return;
    }
    expandCollapseButton.addEventListener('click', (ev) => toggleGroupExpandedAsync(element, navlink, ev));
}

function detachEventHandlers(element) {
    let navlink = element.getElementsByClassName("fluent-nav-link")[0];
    if (!navlink) {
        return;
    }
    if (!navlink.href) {
        navlink.removeEventListener('click', toggleGroupExpandedAsync);
    }
    navlink.removeEventListener('keydown', handleExpanderKeyDownAsync);

    let expandCollapseButton = element.getElementsByClassName("expand-collapse-button")[0];
    expandCollapseButton.removeEventListener('click', toggleGroupExpandedAsync);
}

function toggleMenuExpandedAsync(element, orig, event) {

    let parent = element.parentElement;
    if (!parent.classList.contains('collapsed')) {
        parent.classList.add('collapsed');
        parent.style.width = '40px';
        parent.style.minWidth = '40px';
        parent.ariaExpanded = 'false';
        element.ariaExpanded = 'false';
    }
    else {
        parent.classList.remove('collapsed');
        parent.style.cssText = orig;
        parent.ariaExpanded = 'true';
        element.ariaExpanded = 'true';
    }
    event?.stopPropagation();
}

function toggleGroupExpandedAsync(element, navlink, event) {
    if (navlink && navlink.href) {
        event.preventDefault();
    }
    setExpanded(element, !element.classList.contains('expanded'));
    event?.stopPropagation();
}

function handleExpanderKeyDownAsync(element, event) {
    switch (event.code) {
        case "NumpadEnter":
        case "Enter":
            toggleGroupExpandedAsync(element, null, event);
            break;
        case "NumpadArrowRight":
        case "ArrowRight":
            setExpanded(element, true);
            break;
        case "NumpadArrowLeft":
        case "ArrowLeft":
            setExpanded(element, false);
            break;
    }
    event.stopPropagation();
}

function handleMenuExpanderKeyDownAsync(element, origStyle, event) {
    switch (event.code) {
        case "NumpadEnter":
        case "Enter":
            toggleMenuExpandedAsync(element, origStyle, event);
            break;
        case "NumpadArrowRight":
        case "ArrowRight":
            setMenuExpanded(element, origStyle, true);
            break;
        case "NumpadArrowLeft":
        case "ArrowLeft":
            setMenuExpanded(element, origStyle, false);
            break;
    }
    event.stopPropagation();
}

function setExpanded(element, expand) {
    var collapsibleRegion = element.getElementsByClassName("items")[0];
    var button = element.getElementsByClassName("expand-collapse-button")[0];
    if (expand) {
        button.classList.add("rotate");
        collapsibleRegion.style.height = 'auto';
        element.classList.add('expanded');

    } else {
        button.classList.remove("rotate");
        collapsibleRegion.style.height = '0px';
        element.classList.remove('expanded');
    }
}

function setMenuExpanded(element, origStyle, expand) {
    let parent = element.parentElement;
    if (expand) {
        parent.classList.remove('collapsed');
        parent.style.cssText = origStyle;
        parent.ariaExpanded = 'true';
        element.ariaExpanded = 'true';
    }
    else {
        parent.classList.add('collapsed');
        parent.style.width = '40px';
        parent.style.minWidth = '40px';
        parent.ariaExpanded = 'false';
        element.ariaExpanded = 'false';
    }
}

// SIG // Begin signature block
// SIG // MIIpJgYJKoZIhvcNAQcCoIIpFzCCKRMCAQExDzANBglg
// SIG // hkgBZQMEAgEFADB3BgorBgEEAYI3AgEEoGkwZzAyBgor
// SIG // BgEEAYI3AgEeMCQCAQEEEBDgyQbOONQRoqMAEEvTUJAC
// SIG // AQACAQACAQACAQACAQAwMTANBglghkgBZQMEAgEFAAQg
// SIG // eF/4BdCkdBkGUP8wwoG357r8w9PtC9C84qfGbLEdCqqg
// SIG // gg30MIIGcjCCBFqgAwIBAgITMwAABD8V06Npnu+JzAAA
// SIG // AAAEPzANBgkqhkiG9w0BAQwFADB+MQswCQYDVQQGEwJV
// SIG // UzETMBEGA1UECBMKV2FzaGluZ3RvbjEQMA4GA1UEBxMH
// SIG // UmVkbW9uZDEeMBwGA1UEChMVTWljcm9zb2Z0IENvcnBv
// SIG // cmF0aW9uMSgwJgYDVQQDEx9NaWNyb3NvZnQgQ29kZSBT
// SIG // aWduaW5nIFBDQSAyMDExMB4XDTI0MTExOTE5NTA1OFoX
// SIG // DTI1MTExMjE5NTA1OFowYzELMAkGA1UEBhMCVVMxEzAR
// SIG // BgNVBAgTCldhc2hpbmd0b24xEDAOBgNVBAcTB1JlZG1v
// SIG // bmQxHjAcBgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlv
// SIG // bjENMAsGA1UEAxMELk5FVDCCAaIwDQYJKoZIhvcNAQEB
// SIG // BQADggGPADCCAYoCggGBAKrK/pPcofXVP3BfQdSqPE+k
// SIG // mqCTYPZCWIfNPatBgWatVqw20RnIAApXDJjLQBYCKbOx
// SIG // YpsssP8pqQ0dmYpWuYg8B0T6T3n3bprZRRNoiw4KGRw1
// SIG // 6DrNg3WWc2ubWCmgPK5qvL5iIeiX71x6rZViXbOoQMcx
// SIG // 64pryR1BfiquU/J/G0W8zlB23677yGA5UOWF7tbQhVPZ
// SIG // hHFXtMvgN1YLGYBKG7ifFAeuEp5tD93iUTWDBC87jWbs
// SIG // ESvl91RfO7uFUTO7dfP3LJOwDQAmzvP5wrU+Tc7AHaG6
// SIG // HVEKEnr72PG+O+BjvUHTBY1zejxe6MRqtH0+te1TdDYs
// SIG // 5JeKgFWvXKgYaZQ3pUNtMY8zpxU6R4afY+//Z3mAWJKJ
// SIG // x/RJy1pCB3wkOzZQ0+r0lcg/7bMG/Y0Cu44+ZJQqz8fV
// SIG // C9/0+81ggRmzRqe8pRMVbeHK7SONglRgu2KR/yBDtgng
// SIG // rM1Vch7JWccnDwZkURgc7y8KHZGE5fR57AzFY55bnKrG
// SIG // 1jXqnQIDAQABo4IBgjCCAX4wHwYDVR0lBBgwFgYKKwYB
// SIG // BAGCN0wIAQYIKwYBBQUHAwMwHQYDVR0OBBYEFCzxnqIr
// SIG // 4/zXEW64dVQ6pVziNXX9MFQGA1UdEQRNMEukSTBHMS0w
// SIG // KwYDVQQLEyRNaWNyb3NvZnQgSXJlbGFuZCBPcGVyYXRp
// SIG // b25zIExpbWl0ZWQxFjAUBgNVBAUTDTQ2NDIyMys1MDMz
// SIG // OTIwHwYDVR0jBBgwFoAUSG5k5VAF04KqFzc3IrVtqMp1
// SIG // ApUwVAYDVR0fBE0wSzBJoEegRYZDaHR0cDovL3d3dy5t
// SIG // aWNyb3NvZnQuY29tL3BraW9wcy9jcmwvTWljQ29kU2ln
// SIG // UENBMjAxMV8yMDExLTA3LTA4LmNybDBhBggrBgEFBQcB
// SIG // AQRVMFMwUQYIKwYBBQUHMAKGRWh0dHA6Ly93d3cubWlj
// SIG // cm9zb2Z0LmNvbS9wa2lvcHMvY2VydHMvTWljQ29kU2ln
// SIG // UENBMjAxMV8yMDExLTA3LTA4LmNydDAMBgNVHRMBAf8E
// SIG // AjAAMA0GCSqGSIb3DQEBDAUAA4ICAQCoh/YE7CpG9ULB
// SIG // TlmokhdbFKSf2m6Ezu6Z+Xmn0mnrqabfSlb2WmW2TqJ2
// SIG // k1sn0fZODP1jwbMkWiEEQ7xIZQdSQnEh1Ht08RXWDMgN
// SIG // GSyxlblGoXSeyojaNB6xbUG4bSKP0sHKHs0tOpEoZA8e
// SIG // PzzJUnbKf8KHm847mm+7IL0l8rF/e749WUJREr4JXRds
// SIG // AIOfgtMCgi3QeN/x9PIKudz3GUNrOag76pbmdLRYzZjx
// SIG // ByofpGhvsU2QkSUBn/5q05gZUbSnC9vdxFzbRDt+OMm2
// SIG // DaAju7JxCOSJ3t74yYHeuiexyaq0mD4ioxGdSh/OZ/AO
// SIG // or0eC5bC/O4Kq6+NTT0epjdygN5alndo6Z0tCO0/Fjap
// SIG // NZPWkEJVx0PFfk0cL13Q5YeTnSBEP8a0dtclfpQxo5Fr
// SIG // Mo3l9lIDoNMBQ3F/dD1Txlz/Az72W9OD9/J6c1q3ysJs
// SIG // kAt4AgPdXX1zoOoPQJlCSmb0G3XBHKGYS8PXbejDNteP
// SIG // Xnms+kCKa0+pmqzNtUxl6eNDf7/+TF3gJP+y8kgh1CVo
// SIG // miLfZX8jrSnQ04UjTxU7l3CDWgyTgXGNta2p5k7C3f9x
// SIG // SsPmeVKb8RwPN1tKkQaureSPwuQCq0GYaJj8gO0MlbAx
// SIG // 7N3pF12UNdb9tNwAnq0Nip6rkke4BNucn3M97xdVVeCA
// SIG // P+xyrbPHcjCCB3owggVioAMCAQICCmEOkNIAAAAAAAMw
// SIG // DQYJKoZIhvcNAQELBQAwgYgxCzAJBgNVBAYTAlVTMRMw
// SIG // EQYDVQQIEwpXYXNoaW5ndG9uMRAwDgYDVQQHEwdSZWRt
// SIG // b25kMR4wHAYDVQQKExVNaWNyb3NvZnQgQ29ycG9yYXRp
// SIG // b24xMjAwBgNVBAMTKU1pY3Jvc29mdCBSb290IENlcnRp
// SIG // ZmljYXRlIEF1dGhvcml0eSAyMDExMB4XDTExMDcwODIw
// SIG // NTkwOVoXDTI2MDcwODIxMDkwOVowfjELMAkGA1UEBhMC
// SIG // VVMxEzARBgNVBAgTCldhc2hpbmd0b24xEDAOBgNVBAcT
// SIG // B1JlZG1vbmQxHjAcBgNVBAoTFU1pY3Jvc29mdCBDb3Jw
// SIG // b3JhdGlvbjEoMCYGA1UEAxMfTWljcm9zb2Z0IENvZGUg
// SIG // U2lnbmluZyBQQ0EgMjAxMTCCAiIwDQYJKoZIhvcNAQEB
// SIG // BQADggIPADCCAgoCggIBAKvw+nIQHC6t2G6qghBNNLry
// SIG // tlghn0IbKmvpWlCquAY4GgRJun/DDB7dN2vGEtgL8DjC
// SIG // mQawyDnVARQxQtOJDXlkh36UYCRsr55JnOloXtLfm1Oy
// SIG // CizDr9mpK656Ca/XllnKYBoF6WZ26DJSJhIv56sIUM+z
// SIG // RLdd2MQuA3WraPPLbfM6XKEW9Ea64DhkrG5kNXimoGMP
// SIG // LdNAk/jj3gcN1Vx5pUkp5w2+oBN3vpQ97/vjK1oQH01W
// SIG // KKJ6cuASOrdJXtjt7UORg9l7snuGG9k+sYxd6IlPhBry
// SIG // oS9Z5JA7La4zWMW3Pv4y07MDPbGyr5I4ftKdgCz1TlaR
// SIG // ITUlwzluZH9TupwPrRkjhMv0ugOGjfdf8NBSv4yUh7zA
// SIG // IXQlXxgotswnKDglmDlKNs98sZKuHCOnqWbsYR9q4ShJ
// SIG // nV+I4iVd0yFLPlLEtVc/JAPw0XpbL9Uj43BdD1FGd7P4
// SIG // AOG8rAKCX9vAFbO9G9RVS+c5oQ/pI0m8GLhEfEXkwcNy
// SIG // euBy5yTfv0aZxe/CHFfbg43sTUkwp6uO3+xbn6/83bBm
// SIG // 4sGXgXvt1u1L50kppxMopqd9Z4DmimJ4X7IvhNdXnFy/
// SIG // dygo8e1twyiPLI9AN0/B4YVEicQJTMXUpUMvdJX3bvh4
// SIG // IFgsE11glZo+TzOE2rCIF96eTvSWsLxGoGyY0uDWiIwL
// SIG // AgMBAAGjggHtMIIB6TAQBgkrBgEEAYI3FQEEAwIBADAd
// SIG // BgNVHQ4EFgQUSG5k5VAF04KqFzc3IrVtqMp1ApUwGQYJ
// SIG // KwYBBAGCNxQCBAweCgBTAHUAYgBDAEEwCwYDVR0PBAQD
// SIG // AgGGMA8GA1UdEwEB/wQFMAMBAf8wHwYDVR0jBBgwFoAU
// SIG // ci06AjGQQ7kUBU7h6qfHMdEjiTQwWgYDVR0fBFMwUTBP
// SIG // oE2gS4ZJaHR0cDovL2NybC5taWNyb3NvZnQuY29tL3Br
// SIG // aS9jcmwvcHJvZHVjdHMvTWljUm9vQ2VyQXV0MjAxMV8y
// SIG // MDExXzAzXzIyLmNybDBeBggrBgEFBQcBAQRSMFAwTgYI
// SIG // KwYBBQUHMAKGQmh0dHA6Ly93d3cubWljcm9zb2Z0LmNv
// SIG // bS9wa2kvY2VydHMvTWljUm9vQ2VyQXV0MjAxMV8yMDEx
// SIG // XzAzXzIyLmNydDCBnwYDVR0gBIGXMIGUMIGRBgkrBgEE
// SIG // AYI3LgMwgYMwPwYIKwYBBQUHAgEWM2h0dHA6Ly93d3cu
// SIG // bWljcm9zb2Z0LmNvbS9wa2lvcHMvZG9jcy9wcmltYXJ5
// SIG // Y3BzLmh0bTBABggrBgEFBQcCAjA0HjIgHQBMAGUAZwBh
// SIG // AGwAXwBwAG8AbABpAGMAeQBfAHMAdABhAHQAZQBtAGUA
// SIG // bgB0AC4gHTANBgkqhkiG9w0BAQsFAAOCAgEAZ/KGpZjg
// SIG // VHkaLtPYdGcimwuWEeFjkplCln3SeQyQwWVfLiw++MNy
// SIG // 0W2D/r4/6ArKO79HqaPzadtjvyI1pZddZYSQfYtGUFXY
// SIG // DJJ80hpLHPM8QotS0LD9a+M+By4pm+Y9G6XUtR13lDni
// SIG // 6WTJRD14eiPzE32mkHSDjfTLJgJGKsKKELukqQUMm+1o
// SIG // +mgulaAqPyprWEljHwlpblqYluSD9MCP80Yr3vw70L01
// SIG // 724lruWvJ+3Q3fMOr5kol5hNDj0L8giJ1h/DMhji8MUt
// SIG // zluetEk5CsYKwsatruWy2dsViFFFWDgycScaf7H0J/je
// SIG // LDogaZiyWYlobm+nt3TDQAUGpgEqKD6CPxNNZgvAs031
// SIG // 4Y9/HG8VfUWnduVAKmWjw11SYobDHWM2l4bf2vP48hah
// SIG // mifhzaWX0O5dY0HjWwechz4GdwbRBrF1HxS+YWG18NzG
// SIG // GwS+30HHDiju3mUv7Jf2oVyW2ADWoUa9WfOXpQlLSBCZ
// SIG // gB/QACnFsZulP0V3HjXG0qKin3p6IvpIlR+r+0cjgPWe
// SIG // +L9rt0uX4ut1eBrs6jeZeRhL/9azI2h15q/6/IvrC4Dq
// SIG // aTuv/DDtBEyO3991bWORPdGdVk5Pv4BXIqF4ETIheu9B
// SIG // CrE/+6jMpF3BoYibV3FWTkhFwELJm3ZbCoBIa/15n8G9
// SIG // bW1qyVJzEw16UM0xghqKMIIahgIBATCBlTB+MQswCQYD
// SIG // VQQGEwJVUzETMBEGA1UECBMKV2FzaGluZ3RvbjEQMA4G
// SIG // A1UEBxMHUmVkbW9uZDEeMBwGA1UEChMVTWljcm9zb2Z0
// SIG // IENvcnBvcmF0aW9uMSgwJgYDVQQDEx9NaWNyb3NvZnQg
// SIG // Q29kZSBTaWduaW5nIFBDQSAyMDExAhMzAAAEPxXTo2me
// SIG // 74nMAAAAAAQ/MA0GCWCGSAFlAwQCAQUAoIGuMBkGCSqG
// SIG // SIb3DQEJAzEMBgorBgEEAYI3AgEEMBwGCisGAQQBgjcC
// SIG // AQsxDjAMBgorBgEEAYI3AgEVMC8GCSqGSIb3DQEJBDEi
// SIG // BCAoWIF4k2cbj+VklRVrWlvp7XcekYGxCv8GyewyszXt
// SIG // RjBCBgorBgEEAYI3AgEMMTQwMqAUgBIATQBpAGMAcgBv
// SIG // AHMAbwBmAHShGoAYaHR0cDovL3d3dy5taWNyb3NvZnQu
// SIG // Y29tMA0GCSqGSIb3DQEBAQUABIIBgAT4NMaMLuBVa+ws
// SIG // i0BywrE9Am4hRo2APs3JOllgQvRPo20j76PjnzcDuuQK
// SIG // rYHsdPezDpFn8Hn2r7jS1g8/QIhVYh4LW+Vilhc4OHwR
// SIG // 7asdbJdWdT32YiijJdn1lWdVdc/11yCQbJVwSMgThUo1
// SIG // Ux+tdUuFIA0NoLtYrsrREThyWAEBtbCGXQW/AFyJhGc/
// SIG // 1CqF+33/sQNt9dFdzTJfCQmYrw8NE/ME6HryS/8f41En
// SIG // CYMSzMlh9vxQsTU7qe9xtQ+hoPGhhziMOu7f9tanjfMq
// SIG // Aut31rlnKyAaRRMWFYy8yqJuq6APZc0pSZnqJa33T0H3
// SIG // EMtttjz6ki+gq0zi6veooFYcjfYYZM7wKVbVbgMtSL4m
// SIG // Ry5Bw6/TmP20xLT9cceQdZwrKZK0KENTe+ZI10KUX9OX
// SIG // d+LddD6d6p9C3YZnpjvgVry+R6ro13BLCn8wO1wLFmIi
// SIG // uq58o80mKHGRPtj82veIBpUpeX9v4bur+S/tq1db/4sf
// SIG // Yqe+soKvEwnz3KGCF5QwgheQBgorBgEEAYI3AwMBMYIX
// SIG // gDCCF3wGCSqGSIb3DQEHAqCCF20wghdpAgEDMQ8wDQYJ
// SIG // YIZIAWUDBAIBBQAwggFSBgsqhkiG9w0BCRABBKCCAUEE
// SIG // ggE9MIIBOQIBAQYKKwYBBAGEWQoDATAxMA0GCWCGSAFl
// SIG // AwQCAQUABCC4tL0l6K8yA1WS5nAL3JLJGIFBXDes7wNT
// SIG // Mhi/SHpZIQIGaKOjvAyTGBMyMDI1MTAwMzE3NDk0Mi43
// SIG // ODdaMASAAgH0oIHRpIHOMIHLMQswCQYDVQQGEwJVUzET
// SIG // MBEGA1UECBMKV2FzaGluZ3RvbjEQMA4GA1UEBxMHUmVk
// SIG // bW9uZDEeMBwGA1UEChMVTWljcm9zb2Z0IENvcnBvcmF0
// SIG // aW9uMSUwIwYDVQQLExxNaWNyb3NvZnQgQW1lcmljYSBP
// SIG // cGVyYXRpb25zMScwJQYDVQQLEx5uU2hpZWxkIFRTUyBF
// SIG // U046N0YwMC0wNUUwLUQ5NDcxJTAjBgNVBAMTHE1pY3Jv
// SIG // c29mdCBUaW1lLVN0YW1wIFNlcnZpY2WgghHqMIIHIDCC
// SIG // BQigAwIBAgITMwAAAgbXvFE4mCPsLAABAAACBjANBgkq
// SIG // hkiG9w0BAQsFADB8MQswCQYDVQQGEwJVUzETMBEGA1UE
// SIG // CBMKV2FzaGluZ3RvbjEQMA4GA1UEBxMHUmVkbW9uZDEe
// SIG // MBwGA1UEChMVTWljcm9zb2Z0IENvcnBvcmF0aW9uMSYw
// SIG // JAYDVQQDEx1NaWNyb3NvZnQgVGltZS1TdGFtcCBQQ0Eg
// SIG // MjAxMDAeFw0yNTAxMzAxOTQyNTBaFw0yNjA0MjIxOTQy
// SIG // NTBaMIHLMQswCQYDVQQGEwJVUzETMBEGA1UECBMKV2Fz
// SIG // aGluZ3RvbjEQMA4GA1UEBxMHUmVkbW9uZDEeMBwGA1UE
// SIG // ChMVTWljcm9zb2Z0IENvcnBvcmF0aW9uMSUwIwYDVQQL
// SIG // ExxNaWNyb3NvZnQgQW1lcmljYSBPcGVyYXRpb25zMScw
// SIG // JQYDVQQLEx5uU2hpZWxkIFRTUyBFU046N0YwMC0wNUUw
// SIG // LUQ5NDcxJTAjBgNVBAMTHE1pY3Jvc29mdCBUaW1lLVN0
// SIG // YW1wIFNlcnZpY2UwggIiMA0GCSqGSIb3DQEBAQUAA4IC
// SIG // DwAwggIKAoICAQDpRIWbIM3Rlr397cjHaYx85l7I+ZVW
// SIG // GMCBCM911BpU6+IGWCqksqgqefZFEjKzNVDYC9YcgITA
// SIG // z276NGgvECm4ZfNv/FPwcaSDz7xbDbsOoxbwQoHUNRro
// SIG // +x5ubZhT6WJeU97F06+vDjAw/Yt1vWOgRTqmP/dNr9oq
// SIG // IbE5oCLYdH3wI/noYmsJVc7966n+B7UAGAWU2se3Lz+x
// SIG // dxnNsNX4CR6zIMVJTSezP/2STNcxJTu9k2sl7/vzOhxJ
// SIG // hCQ38rdaEoqhGHrXrmVkEhSv+S00DMJc1OIXxqfbwPjM
// SIG // qEVp7K3kmczCkbum1BOIJ2wuDAbKuJelpteNZj/S58NS
// SIG // Qw6khfuJAluqHK3igkS/Oux49qTP+rU+PQeNuD+GtrCo
// SIG // pFucRmanQvxISGNoxnBq3UeDTqphm6aI7GMHtFD6DOjJ
// SIG // lllH1gVWXPTyivf+4tN8TmO6yIgB4uP00bH9jn/dyyxS
// SIG // jxPQ2nGvZtgtqnvq3h3TRjRnkc+e1XB1uatDa1zUcS7r
// SIG // 3iodTpyATe2hgkVX3m4DhRzI6A4SJ6fbJM9isLH8AGKc
// SIG // ymisKzYupAeFSTJ10JEFa6MjHQYYohoCF77R0CCwMNjv
// SIG // E4XfLHu+qKPY8GQfsZdigQ9clUAiydFmVt61hytoxZP7
// SIG // LmXbzjD0VecyzZoL4Equ1XszBsulAr5Ld2KwcwIDAQAB
// SIG // o4IBSTCCAUUwHQYDVR0OBBYEFO0wsLKdDGpT97cx3Iym
// SIG // yo/SBm4SMB8GA1UdIwQYMBaAFJ+nFV0AXmJdg/Tl0mWn
// SIG // G1M1GelyMF8GA1UdHwRYMFYwVKBSoFCGTmh0dHA6Ly93
// SIG // d3cubWljcm9zb2Z0LmNvbS9wa2lvcHMvY3JsL01pY3Jv
// SIG // c29mdCUyMFRpbWUtU3RhbXAlMjBQQ0ElMjAyMDEwKDEp
// SIG // LmNybDBsBggrBgEFBQcBAQRgMF4wXAYIKwYBBQUHMAKG
// SIG // UGh0dHA6Ly93d3cubWljcm9zb2Z0LmNvbS9wa2lvcHMv
// SIG // Y2VydHMvTWljcm9zb2Z0JTIwVGltZS1TdGFtcCUyMFBD
// SIG // QSUyMDIwMTAoMSkuY3J0MAwGA1UdEwEB/wQCMAAwFgYD
// SIG // VR0lAQH/BAwwCgYIKwYBBQUHAwgwDgYDVR0PAQH/BAQD
// SIG // AgeAMA0GCSqGSIb3DQEBCwUAA4ICAQB23GZOfe9ThTUv
// SIG // D29i4t6lDpxJhpVRMme+UbyZhBFCZhoGTtjDdphAArU2
// SIG // Q61WYg3YVcl2RdJm5PUbZ2bA77zk+qtLxC+3dNxVsTcd
// SIG // txPDSSWgwBHxTj6pCmoDNXolAYsWpvHQFCHDqEfAiBxX
// SIG // 1dmaXbiTP1d0XffvgR6dshUcqaH/mFfjDZAxLU1s6HcV
// SIG // gCvBQJlJ7xEG5jFKdtqapKWcbUHwTVqXQGbIlHVClNJ3
// SIG // yqW6Z3UJH/CFcYiLV/e68urTmGtiZxGSYb4SBSPArTrT
// SIG // YeHOlQIj/7loVWmfWX2y4AGV/D+MzyZMyvFw4VyL0Vgq
// SIG // 96EzQKyteiVeBaVEjxQKo3AcPULRF4Uzz98P2tCM5XbF
// SIG // Z3Qoj9PLg3rgFXr0oJEhfh2tqUrhTJd13+i4/fek9zWi
// SIG // coshlwXgFu002ZWBVzASEFuqED48qyulZ/2jGJBcta+F
// SIG // dk2loP2K3oSj4PQQe1MzzVZO52AXO42MHlhm3SHo3/Rh
// SIG // Q+I1A0Ny+9uAehkQH6LrxkrVNvZG4f0PAKMbqUcXG7xz
// SIG // nKJ0x0HYr5ayWGbHKZRcObU+/34ZpL9NrXOedVDXmSd2
// SIG // ylKSl/vvi1QwNJqXJl/+gJkQEetqmHAUFQkFtemi8MUX
// SIG // QG2w/RDHXXwWAjE+qIDZLQ/k4z2Z216tWaR6RDKHGkwe
// SIG // CoDtQtzkHTCCB3EwggVZoAMCAQICEzMAAAAVxedrngKb
// SIG // SZkAAAAAABUwDQYJKoZIhvcNAQELBQAwgYgxCzAJBgNV
// SIG // BAYTAlVTMRMwEQYDVQQIEwpXYXNoaW5ndG9uMRAwDgYD
// SIG // VQQHEwdSZWRtb25kMR4wHAYDVQQKExVNaWNyb3NvZnQg
// SIG // Q29ycG9yYXRpb24xMjAwBgNVBAMTKU1pY3Jvc29mdCBS
// SIG // b290IENlcnRpZmljYXRlIEF1dGhvcml0eSAyMDEwMB4X
// SIG // DTIxMDkzMDE4MjIyNVoXDTMwMDkzMDE4MzIyNVowfDEL
// SIG // MAkGA1UEBhMCVVMxEzARBgNVBAgTCldhc2hpbmd0b24x
// SIG // EDAOBgNVBAcTB1JlZG1vbmQxHjAcBgNVBAoTFU1pY3Jv
// SIG // c29mdCBDb3Jwb3JhdGlvbjEmMCQGA1UEAxMdTWljcm9z
// SIG // b2Z0IFRpbWUtU3RhbXAgUENBIDIwMTAwggIiMA0GCSqG
// SIG // SIb3DQEBAQUAA4ICDwAwggIKAoICAQDk4aZM57RyIQt5
// SIG // osvXJHm9DtWC0/3unAcH0qlsTnXIyjVX9gF/bErg4r25
// SIG // PhdgM/9cT8dm95VTcVrifkpa/rg2Z4VGIwy1jRPPdzLA
// SIG // EBjoYH1qUoNEt6aORmsHFPPFdvWGUNzBRMhxXFExN6AK
// SIG // OG6N7dcP2CZTfDlhAnrEqv1yaa8dq6z2Nr41JmTamDu6
// SIG // GnszrYBbfowQHJ1S/rboYiXcag/PXfT+jlPP1uyFVk3v
// SIG // 3byNpOORj7I5LFGc6XBpDco2LXCOMcg1KL3jtIckw+DJ
// SIG // j361VI/c+gVVmG1oO5pGve2krnopN6zL64NF50ZuyjLV
// SIG // wIYwXE8s4mKyzbnijYjklqwBSru+cakXW2dg3viSkR4d
// SIG // Pf0gz3N9QZpGdc3EXzTdEonW/aUgfX782Z5F37ZyL9t9
// SIG // X4C626p+Nuw2TPYrbqgSUei/BQOj0XOmTTd0lBw0gg/w
// SIG // EPK3Rxjtp+iZfD9M269ewvPV2HM9Q07BMzlMjgK8Qmgu
// SIG // EOqEUUbi0b1qGFphAXPKZ6Je1yh2AuIzGHLXpyDwwvoS
// SIG // CtdjbwzJNmSLW6CmgyFdXzB0kZSU2LlQ+QuJYfM2BjUY
// SIG // hEfb3BvR/bLUHMVr9lxSUV0S2yW6r1AFemzFER1y7435
// SIG // UsSFF5PAPBXbGjfHCBUYP3irRbb1Hode2o+eFnJpxq57
// SIG // t7c+auIurQIDAQABo4IB3TCCAdkwEgYJKwYBBAGCNxUB
// SIG // BAUCAwEAATAjBgkrBgEEAYI3FQIEFgQUKqdS/mTEmr6C
// SIG // kTxGNSnPEP8vBO4wHQYDVR0OBBYEFJ+nFV0AXmJdg/Tl
// SIG // 0mWnG1M1GelyMFwGA1UdIARVMFMwUQYMKwYBBAGCN0yD
// SIG // fQEBMEEwPwYIKwYBBQUHAgEWM2h0dHA6Ly93d3cubWlj
// SIG // cm9zb2Z0LmNvbS9wa2lvcHMvRG9jcy9SZXBvc2l0b3J5
// SIG // Lmh0bTATBgNVHSUEDDAKBggrBgEFBQcDCDAZBgkrBgEE
// SIG // AYI3FAIEDB4KAFMAdQBiAEMAQTALBgNVHQ8EBAMCAYYw
// SIG // DwYDVR0TAQH/BAUwAwEB/zAfBgNVHSMEGDAWgBTV9lbL
// SIG // j+iiXGJo0T2UkFvXzpoYxDBWBgNVHR8ETzBNMEugSaBH
// SIG // hkVodHRwOi8vY3JsLm1pY3Jvc29mdC5jb20vcGtpL2Ny
// SIG // bC9wcm9kdWN0cy9NaWNSb29DZXJBdXRfMjAxMC0wNi0y
// SIG // My5jcmwwWgYIKwYBBQUHAQEETjBMMEoGCCsGAQUFBzAC
// SIG // hj5odHRwOi8vd3d3Lm1pY3Jvc29mdC5jb20vcGtpL2Nl
// SIG // cnRzL01pY1Jvb0NlckF1dF8yMDEwLTA2LTIzLmNydDAN
// SIG // BgkqhkiG9w0BAQsFAAOCAgEAnVV9/Cqt4SwfZwExJFvh
// SIG // nnJL/Klv6lwUtj5OR2R4sQaTlz0xM7U518JxNj/aZGx8
// SIG // 0HU5bbsPMeTCj/ts0aGUGCLu6WZnOlNN3Zi6th542DYu
// SIG // nKmCVgADsAW+iehp4LoJ7nvfam++Kctu2D9IdQHZGN5t
// SIG // ggz1bSNU5HhTdSRXud2f8449xvNo32X2pFaq95W2KFUn
// SIG // 0CS9QKC/GbYSEhFdPSfgQJY4rPf5KYnDvBewVIVCs/wM
// SIG // nosZiefwC2qBwoEZQhlSdYo2wh3DYXMuLGt7bj8sCXgU
// SIG // 6ZGyqVvfSaN0DLzskYDSPeZKPmY7T7uG+jIa2Zb0j/aR
// SIG // AfbOxnT99kxybxCrdTDFNLB62FD+CljdQDzHVG2dY3RI
// SIG // LLFORy3BFARxv2T5JL5zbcqOCb2zAVdJVGTZc9d/HltE
// SIG // AY5aGZFrDZ+kKNxnGSgkujhLmm77IVRrakURR6nxt67I
// SIG // 6IleT53S0Ex2tVdUCbFpAUR+fKFhbHP+CrvsQWY9af3L
// SIG // wUFJfn6Tvsv4O+S3Fb+0zj6lMVGEvL8CwYKiexcdFYmN
// SIG // cP7ntdAoGokLjzbaukz5m/8K6TT4JDVnK+ANuOaMmdbh
// SIG // IurwJ0I9JZTmdHRbatGePu1+oDEzfbzL6Xu/OHBE0ZDx
// SIG // yKs6ijoIYn/ZcGNTTY3ugm2lBRDBcQZqELQdVTNYs6Fw
// SIG // ZvKhggNNMIICNQIBATCB+aGB0aSBzjCByzELMAkGA1UE
// SIG // BhMCVVMxEzARBgNVBAgTCldhc2hpbmd0b24xEDAOBgNV
// SIG // BAcTB1JlZG1vbmQxHjAcBgNVBAoTFU1pY3Jvc29mdCBD
// SIG // b3Jwb3JhdGlvbjElMCMGA1UECxMcTWljcm9zb2Z0IEFt
// SIG // ZXJpY2EgT3BlcmF0aW9uczEnMCUGA1UECxMeblNoaWVs
// SIG // ZCBUU1MgRVNOOjdGMDAtMDVFMC1EOTQ3MSUwIwYDVQQD
// SIG // ExxNaWNyb3NvZnQgVGltZS1TdGFtcCBTZXJ2aWNloiMK
// SIG // AQEwBwYFKw4DAhoDFQAEa0f118XHM/VNdqKBs4QXxNnN
// SIG // 96CBgzCBgKR+MHwxCzAJBgNVBAYTAlVTMRMwEQYDVQQI
// SIG // EwpXYXNoaW5ndG9uMRAwDgYDVQQHEwdSZWRtb25kMR4w
// SIG // HAYDVQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xJjAk
// SIG // BgNVBAMTHU1pY3Jvc29mdCBUaW1lLVN0YW1wIFBDQSAy
// SIG // MDEwMA0GCSqGSIb3DQEBCwUAAgUA7IoY7TAiGA8yMDI1
// SIG // MTAwMzA5NDIwNVoYDzIwMjUxMDA0MDk0MjA1WjB0MDoG
// SIG // CisGAQQBhFkKBAExLDAqMAoCBQDsihjtAgEAMAcCAQAC
// SIG // AjtMMAcCAQACAhG4MAoCBQDsi2ptAgEAMDYGCisGAQQB
// SIG // hFkKBAIxKDAmMAwGCisGAQQBhFkKAwKgCjAIAgEAAgMH
// SIG // oSChCjAIAgEAAgMBhqAwDQYJKoZIhvcNAQELBQADggEB
// SIG // AE7HcD9qK2h3dkyA1yrofYCFe9m9LITiYaJACCeak2Hc
// SIG // Y18656rinNb5MPRrjLtstDuCtSoz62RM0+W+5hClrG3O
// SIG // frpzDc0NHpZNN7ZBSl6wYPFh9qGRT5bSHYZviJ7PNzeF
// SIG // 7zDslZa7LcvWfAUipGHJ12GlMhNArQUWOpmQCOpLk0N6
// SIG // 3CdE7LGqa+J54SZF3eDdU0NoAj+k1JsfgMehTW2JFC25
// SIG // +kyOZWGe+MCohmHp+cynTwTA+LA1zUjaZIkVPhVTrilz
// SIG // D3E4f25E+70Ol8P3tIyMMDZpfkMA4T33TWpILnqreGB/
// SIG // QZccFh/2RfBgvbsz+1nwizt6opHYFv+mp8cxggQNMIIE
// SIG // CQIBATCBkzB8MQswCQYDVQQGEwJVUzETMBEGA1UECBMK
// SIG // V2FzaGluZ3RvbjEQMA4GA1UEBxMHUmVkbW9uZDEeMBwG
// SIG // A1UEChMVTWljcm9zb2Z0IENvcnBvcmF0aW9uMSYwJAYD
// SIG // VQQDEx1NaWNyb3NvZnQgVGltZS1TdGFtcCBQQ0EgMjAx
// SIG // MAITMwAAAgbXvFE4mCPsLAABAAACBjANBglghkgBZQME
// SIG // AgEFAKCCAUowGgYJKoZIhvcNAQkDMQ0GCyqGSIb3DQEJ
// SIG // EAEEMC8GCSqGSIb3DQEJBDEiBCAS7UP/g9hKhF9mrjgJ
// SIG // 0jpyeKY6WhKTkmXzK6lBIR0WNjCB+gYLKoZIhvcNAQkQ
// SIG // Ai8xgeowgecwgeQwgb0EIODo9ZSIkZ6dVtKT+E/uZx2W
// SIG // Ay7KiXM5R1JIOhNJf0vSMIGYMIGApH4wfDELMAkGA1UE
// SIG // BhMCVVMxEzARBgNVBAgTCldhc2hpbmd0b24xEDAOBgNV
// SIG // BAcTB1JlZG1vbmQxHjAcBgNVBAoTFU1pY3Jvc29mdCBD
// SIG // b3Jwb3JhdGlvbjEmMCQGA1UEAxMdTWljcm9zb2Z0IFRp
// SIG // bWUtU3RhbXAgUENBIDIwMTACEzMAAAIG17xROJgj7CwA
// SIG // AQAAAgYwIgQghuohIC45mor6WnFWC9TAlvrpWqzGdUbb
// SIG // TURbHP36mpQwDQYJKoZIhvcNAQELBQAEggIAGAqjtzXr
// SIG // V4b9YQh2kOS52vFZC6KHg3yWwD5wIE5BmJrDRyTWhZnC
// SIG // 0wa7SgHIDoLBbK5l60OWF6elxyo5aE0ilSS4CZk+Mz1b
// SIG // dW6BkAzBiI10kN2e29nnK1T1jwlYojLEngQbXG/CPmen
// SIG // 6f6Q2D1PmJW7d96jaUb+epAsYohiOcoawrC4Z63sOa+P
// SIG // 3N3gTD4flL7Px5H5lLdIZJZ5ErpKyBiU+G/qfL73WA0O
// SIG // YPOm8GCaUzCgp68hQJDb5jhLwlnaEDU50fn3YITXSU+K
// SIG // Oh8n9/BoNio6c75DJ5t6vuuYAsbsVkmdyDMtU5AxXztp
// SIG // ABS8bmzVy7EuRs0K8TSC8MmVPQvHQxUOSbrT9b07mjRJ
// SIG // jFyKXeh25gAHCVoLHuCQBU70omS2UzJycjIMqXjILUYa
// SIG // FC1R3yXnLYeGU27VtvUw48PQA2PI4TsP2of9e1Rxtqub
// SIG // QuLE58Rcs3MAxDZVNxZ9uQws7jkhP3XJOAcLW7DNDMl+
// SIG // UjXoYHTfuGb31+jILPKJVNFqq+tY/dRIsSl8qcGo89S7
// SIG // LolH1yYjhcX4lwEd+45xS8oVp5RkPNyOJ+Jt+mhtwuE8
// SIG // Kp5ZRclq6z0RFPeJjCzWCOQB1UX9ItPOGnE8IAKnSUZ/
// SIG // hmr9qDMqSft7iTieyfLS4P/jFPm+jfjq/7hro8NBgTes
// SIG // e3JN5MhIu54qJBU=
// SIG // End signature block
