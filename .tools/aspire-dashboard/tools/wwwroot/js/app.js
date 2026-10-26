
// To avoid Flash of Unstyled Content, the body is hidden by default with
// the before-upgrade CSS class. Here we'll find the first web component
// and wait for it to be upgraded. When it is, we'll remove that class
// from the body.
const firstUndefinedElement = document.body.querySelector(":not(:defined)");

if (firstUndefinedElement) {
    customElements.whenDefined(firstUndefinedElement.localName).then(() => {
        document.body.classList.remove("before-upgrade");
    });
} else {
    // In the event this code doesn't run until after they've all been upgraded
    document.body.classList.remove("before-upgrade");
}

function isElementTagName(element, tagName) {
    return element.tagName.toLowerCase() === tagName;
}

function getFluentMenuItemForTarget(element) {
    // User could have clicked on either a path or svg (the image on the item) or the item itself
    if (isElementTagName(element, "path")) {
        return getFluentMenuItemForTarget(element.parentElement);
    }

    // in between the svg and fluent-menu-item is a span for the icon slot
    const possibleMenuItem = element.parentElement?.parentElement;
    if (possibleMenuItem && (isElementTagName(possibleMenuItem, "fluent-menu-item") || isElementTagName(possibleMenuItem, "button"))) {
        return element.parentElement.parentElement;
    }

    if (isElementTagName(element, "fluent-menu-item") || isElementTagName(element, "button")) {
        return element;
    }

    return null;
}

// Register a global click event listener to handle copy/open button clicks.
// Required because an "onclick" attribute is denied by CSP.
document.addEventListener("click", function (e) {
    // The copy 'button' could either be a button or a menu item.
    const targetElement = isElementTagName(e.target, "fluent-button") ? e.target : getFluentMenuItemForTarget(e.target);
    if (targetElement) {
        if (targetElement.getAttribute("data-copybutton")) {
            buttonCopyTextToClipboard(targetElement);
        } else if (targetElement.getAttribute("data-openbutton")) {
            buttonOpenLink(targetElement);
        }
        e.stopPropagation();
    }
});

let isScrolledToContent = false;
let lastScrollHeight = null;

window.getIsScrolledToContent = function () {
    return isScrolledToContent;
}

window.setIsScrolledToContent = function (value) {
    if (isScrolledToContent != value) {
        isScrolledToContent = value;
        console.log(`isScrolledToContent=${isScrolledToContent}`);
    }
}

window.resetContinuousScrollPosition = function () {
    // Reset to scrolling to the end of the content after switching.
    setIsScrolledToContent(false);
}

window.initializeContinuousScroll = function () {
    // Reset to scrolling to the end of the content when initializing.
    // This needs to be called because the value is remembered across Aspire pages because the browser isn't reloading.
    resetContinuousScrollPosition();

    const container = document.querySelector('.continuous-scroll-overflow');
    if (container == null) {
        return;
    }

    // The scroll event is used to detect when the user scrolls to view content.
    container.addEventListener('scroll', () => {
        var atBottom = isScrolledToBottom(container);
        if (atBottom === null) {
            return;
        }
        setIsScrolledToContent(!atBottom);
   }, { passive: true });

    // The ResizeObserver reports changes in the grid size.
    // This ensures that the logs are scrolled to the bottom when there are new logs
    // unless the user has scrolled to view content.
    const observer = new ResizeObserver(function () {
        lastScrollHeight = container.scrollHeight;

        if (lastScrollHeight == container.clientHeight) {
            // There is no scrollbar. This could be because there's no content, or the content might have been cleared.
            // Reset to default behavior: scroll to bottom
            setIsScrolledToContent(false);
            return;
        }

        var isScrolledToContent = getIsScrolledToContent();
        if (!isScrolledToContent) {
            container.scrollTop = lastScrollHeight;
            return;
        }
    });
    for (const child of container.children) {
        observer.observe(child);
    }
};

function isScrolledToBottom(container) {
    lastScrollHeight = lastScrollHeight || container.scrollHeight

    // There can be a race between resizing and scrolling events.
    // Use the last scroll height from the resize event to figure out if we've scrolled to the bottom.
    if (!getIsScrolledToContent()) {
        if (lastScrollHeight != container.scrollHeight) {
            console.log(`lastScrollHeight ${lastScrollHeight} doesn't equal container scrollHeight ${container.scrollHeight}.`);

            // Unknown because the container size changed.
            return null;
        }
    }

    const marginOfError = 5;
    const containerScrollBottom = lastScrollHeight - container.clientHeight;
    const difference = containerScrollBottom - container.scrollTop;

    var atBottom = difference < marginOfError;
    return atBottom;
}

window.buttonOpenLink = function (element) {
    const url = element.getAttribute("data-url");
    const target = element.getAttribute("data-target");

    window.open(url, target, "noopener,noreferrer");
}

window.buttonCopyTextToClipboard = function(element) {
    const text = element.getAttribute("data-text");
    const precopy = element.getAttribute("data-precopy");
    const postcopy = element.getAttribute("data-postcopy");

    copyTextToClipboard(element.getAttribute("id"), text, precopy, postcopy);
}

window.copyTextToClipboard = function (id, text, precopy, postcopy) {
    const button = document.getElementById(id);

    // If there is a pending timeout then clear it. Otherwise the pending timeout will prematurely reset values.
    if (button.dataset.copyTimeout) {
        clearTimeout(button.dataset.copyTimeout);
        delete button.dataset.copyTimeout;
    }

    const copyIcon = button.querySelector('.copy-icon');
    const checkmarkIcon = button.querySelector('.checkmark-icon');

    const anchoredTooltip = document.querySelector(`fluent-tooltip[anchor="${id}"]`);
    const tooltipDiv = anchoredTooltip ? anchoredTooltip.children[0] : null;
    navigator.clipboard.writeText(text)
        .then(() => {
            if (tooltipDiv) {
                tooltipDiv.innerText = postcopy;
            }
            if (copyIcon && checkmarkIcon) {
                copyIcon.style.display = 'none';
                checkmarkIcon.style.display = '';
            }
        })
        .catch(() => {
            if (tooltipDiv) {
                tooltipDiv.innerText = 'Could not access clipboard';
            }
        });

    button.dataset.copyTimeout = setTimeout(function () {
        if (tooltipDiv) {
            tooltipDiv.innerText = precopy;
        }

        if (copyIcon && checkmarkIcon) {
            copyIcon.style.display = '';
            checkmarkIcon.style.display = 'none';
        }
        delete button.dataset.copyTimeout;
    }, 1500);
};

function isActiveElementInput() {
    const currentElement = document.activeElement;
    const tagName = currentElement.tagName.toLowerCase();

    // fluent components may have shadow roots that contain inputs
    return tagName === "input" || tagName === "textarea" || tagName.startsWith("fluent") ? isInputElement(currentElement, false) : false;
}

function isInputElement(element, isRoot, isShadowRoot) {
    const tag = element.tagName.toLowerCase();
    // comes from https://developer.mozilla.org/en-US/docs/Web/API/Element/input_event
    // fluent-select does not use <select /> element
    if (tag === "input" || tag === "textarea" || tag === "select" || tag === "fluent-select") {
        return true;
    }

    if (isShadowRoot || isRoot) {
        const elementChildren = element.children;
        for (let i = 0; i < elementChildren.length; i++) {
            if (isInputElement(elementChildren[i], false, isShadowRoot)) {
                return true;
            }
        }
    }

    const shadowRoot = element.shadowRoot;
    if (shadowRoot) {
        const shadowRootChildren = shadowRoot.children;
        for (let i = 0; i < shadowRootChildren.length; i++) {
            if (isInputElement(shadowRootChildren[i], false, true)) {
                return true;
            }
        }
    }

    return false;
}

window.registerGlobalKeydownListener = function (shortcutManager) {
    function hasNoModifiers(keyboardEvent) {
        return !keyboardEvent.altKey && !keyboardEvent.ctrlKey && !keyboardEvent.metaKey && !keyboardEvent.shiftKey;
    }

    // Shift in some but not all, keyboard layouts, is used for + and -
    function modifierKeysExceptShiftNotPressed(keyboardEvent) {
        return !keyboardEvent.altKey && !keyboardEvent.ctrlKey && !keyboardEvent.metaKey;
    }

    function calculateShortcut(e) {
        if (modifierKeysExceptShiftNotPressed(e)) {
            /* general shortcuts */
            switch (e.key) {
                case "?": // help
                    return 100;
                case "S": // settings
                    return 110;

                /* panel shortcuts */
                case "T": // toggle panel orientation
                    return 300;
                case "X": // close panel
                    return 310;
                case "R": // reset panel sizes
                    return 320;
                case "+": // increase panel size
                    return 330;
                case "_": // decrease panel size
                case "-":
                    return 340;
            }
        }

        if (hasNoModifiers(e)) {
            switch (e.key) {
                case "r": // go to resources
                    return 200;
                case "c": // go to console logs
                    return 210;
                case "s": // go to structured logs
                    return 220;
                case "t": // go to traces
                    return 230;
                case "m": // go to metrics
                    return 240;
            }
        }

        return null;
    }

    const keydownListener = function (e) {
        if (isActiveElementInput()) {
            return;
        }

        // list of shortcut enum codes is in src/Aspire.Dashboard/Model/IGlobalKeydownListener.cs
        // to serialize an enum from js->dotnet, we must pass the enum's integer value, not its name
        let shortcut = calculateShortcut(e);

        if (shortcut) {
            shortcutManager.invokeMethodAsync('OnGlobalKeyDown', shortcut);
        }
    }

    window.document.addEventListener('keydown', keydownListener);

    return {
        keydownListener: keydownListener,
    }
};

window.unregisterGlobalKeydownListener = function (obj) {
    window.document.removeEventListener('keydown', obj.keydownListener);
};

window.getBrowserInfo = function () {
    const options = Intl.DateTimeFormat().resolvedOptions();

    return {
        timeZone: options.timeZone,
        userAgent: navigator.userAgent
    };
};

window.focusElement = function (selector) {
    const element = document.getElementById(selector);
    if (element) {
        element.focus();
    }
};

window.getWindowDimensions = function() {
    return {
        width: window.innerWidth,
        height: window.innerHeight
    }
}

window.listenToWindowResize = function(dotnetHelper) {
    function throttle(func, timeout) {
        let currentTimeout = null;
        return function () {
            if (currentTimeout) {
                return;
            }
            const context = this;
            const args = arguments;
            const later = () => {
                func.call(context, ...args);
                currentTimeout = null;
            }
            currentTimeout = setTimeout(later, timeout);
        }
    }

    const throttledResizeListener = throttle(() => {
        dotnetHelper.invokeMethodAsync('OnResizeAsync', { width: window.innerWidth, height: window.innerHeight });
    }, 150)

    window.addEventListener('load', throttledResizeListener);

    window.addEventListener('resize', throttledResizeListener);
}

window.setCellTextClickHandler = function (id) {
    var cellTextElement = document.getElementById(id);
    if (!cellTextElement) {
        return;
    }

    cellTextElement.addEventListener('click', e => {
        // Propagation behavior:
        // - Link click stops. Link will open in a new window.
        // - Any other text allows propagation. Potentially opens details view.
        if (isElementTagName(e.target, 'a')) {
            e.stopPropagation();
        }
    });
};

window.scrollToTop = function (selector) {
    var element = document.querySelector(selector);
    if (element) {
        element.scrollTop = 0;
    }
};

// taken from https://learn.microsoft.com/en-us/aspnet/core/blazor/file-downloads?view=aspnetcore-8.0#download-from-a-stream
window.downloadStreamAsFile = async function (fileName, contentStreamReference) {
    const arrayBuffer = await contentStreamReference.arrayBuffer();
    const blob = new Blob([arrayBuffer]);
    const url = URL.createObjectURL(blob);
    const anchorElement = document.createElement('a');
    anchorElement.href = url;
    anchorElement.download = fileName ?? '';
    anchorElement.click();
    anchorElement.remove();
    URL.revokeObjectURL(url);
};

window.attachChatClickEvent = function (containerId, interop) {
    var container = document.getElementById(containerId);
    if (!container) {
        console.log(`Couldn't find container '${containerId}'.`);
        return;
    }

    container.addEventListener('click', function (event) {
        let anchorElement = event.target.closest('a');
        if (anchorElement) {
            // Only intercept if the link's host matches the current window's host (same domain)
            if (anchorElement.host === window.location.host) {
                event.preventDefault();
                console.log('Link click intercepted:', anchorElement.href);
                interop.invokeMethodAsync('NavigateUrl', anchorElement.href);
            }
        }
    });
}

// SIG // Begin signature block
// SIG // MIIpFwYJKoZIhvcNAQcCoIIpCDCCKQQCAQExDzANBglg
// SIG // hkgBZQMEAgEFADB3BgorBgEEAYI3AgEEoGkwZzAyBgor
// SIG // BgEEAYI3AgEeMCQCAQEEEBDgyQbOONQRoqMAEEvTUJAC
// SIG // AQACAQACAQACAQACAQAwMTANBglghkgBZQMEAgEFAAQg
// SIG // P7xEQIaM8PE9E6vFwKKnL4MxCTfh8sjSbyELuq0neuug
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
// SIG // TkhFwELJm3ZbCoBIa/15n8G9bW1qyVJzEw16UM0xghqK
// SIG // MIIahgIBATCBlTB+MQswCQYDVQQGEwJVUzETMBEGA1UE
// SIG // CBMKV2FzaGluZ3RvbjEQMA4GA1UEBxMHUmVkbW9uZDEe
// SIG // MBwGA1UEChMVTWljcm9zb2Z0IENvcnBvcmF0aW9uMSgw
// SIG // JgYDVQQDEx9NaWNyb3NvZnQgQ29kZSBTaWduaW5nIFBD
// SIG // QSAyMDExAhMzAAAEQBT8C+g+8SRfAAAAAARAMA0GCWCG
// SIG // SAFlAwQCAQUAoIGuMBkGCSqGSIb3DQEJAzEMBgorBgEE
// SIG // AYI3AgEEMBwGCisGAQQBgjcCAQsxDjAMBgorBgEEAYI3
// SIG // AgEVMC8GCSqGSIb3DQEJBDEiBCDNJGn+xp36ZWyolERs
// SIG // KZq+8GWc63x/E7Y1ibi9KLxwQTBCBgorBgEEAYI3AgEM
// SIG // MTQwMqAUgBIATQBpAGMAcgBvAHMAbwBmAHShGoAYaHR0
// SIG // cDovL3d3dy5taWNyb3NvZnQuY29tMA0GCSqGSIb3DQEB
// SIG // AQUABIIBgJl0mwqKI5CjukgnLjXSkPfr8u6CZg1Grcbl
// SIG // 3opNIGR2iB7HWFafhB/iXnVEDBW0lA2QWQvuh3qtAecO
// SIG // NF7czQeQwvnfNYkEp0SCojKlQJd1DOcsdkKs9aDMDPYk
// SIG // dpeDcNWaVo/gwK/ppBYWkyhtA+SZM+3tMgsX0BjnKyKv
// SIG // 1yyqX/usRahmQws1z2TfnHm/rXAFd1IaTSFrpqp2C/dg
// SIG // N9J5/Z9pfdgfuqWM/2/5Wgp2Q3l8jqzH6kyKJ5fLukbR
// SIG // KORWu9uPR6Mx7IY0DQBBx1A3r+lldFol+bG3QN5w3vnI
// SIG // EV0HVKIEPAz0KOyo8SUzo90AT4DiACCjmuvJJA+QVdG0
// SIG // VgXC75IYupzFykYgsUiGxc1QxtARWi18ougnZFEaTC0k
// SIG // D/nxiRyyurGxnTxfOn98LMj1G3MN9r13JVGAZMr8ulJr
// SIG // 2UQkmxy+haqYMyTTKcncakVzo8G9Et9Un6kt2F+tyN9w
// SIG // 6nSfFRMEcKt7lJh0LM/axK/sOOtReNADG+VH26GCF5Qw
// SIG // gheQBgorBgEEAYI3AwMBMYIXgDCCF3wGCSqGSIb3DQEH
// SIG // AqCCF20wghdpAgEDMQ8wDQYJYIZIAWUDBAIBBQAwggFS
// SIG // BgsqhkiG9w0BCRABBKCCAUEEggE9MIIBOQIBAQYKKwYB
// SIG // BAGEWQoDATAxMA0GCWCGSAFlAwQCAQUABCChLGZBfR5C
// SIG // rKEZ4Y67o8iPy63OlXqJlGlNECqPfceIVgIGaNsNPXv5
// SIG // GBMyMDI1MTAwMzE3NTEyMi4wODNaMASAAgH0oIHRpIHO
// SIG // MIHLMQswCQYDVQQGEwJVUzETMBEGA1UECBMKV2FzaGlu
// SIG // Z3RvbjEQMA4GA1UEBxMHUmVkbW9uZDEeMBwGA1UEChMV
// SIG // TWljcm9zb2Z0IENvcnBvcmF0aW9uMSUwIwYDVQQLExxN
// SIG // aWNyb3NvZnQgQW1lcmljYSBPcGVyYXRpb25zMScwJQYD
// SIG // VQQLEx5uU2hpZWxkIFRTUyBFU046RjAwMi0wNUUwLUQ5
// SIG // NDcxJTAjBgNVBAMTHE1pY3Jvc29mdCBUaW1lLVN0YW1w
// SIG // IFNlcnZpY2WgghHqMIIHIDCCBQigAwIBAgITMwAAAgU8
// SIG // dWyCRIfN/gABAAACBTANBgkqhkiG9w0BAQsFADB8MQsw
// SIG // CQYDVQQGEwJVUzETMBEGA1UECBMKV2FzaGluZ3RvbjEQ
// SIG // MA4GA1UEBxMHUmVkbW9uZDEeMBwGA1UEChMVTWljcm9z
// SIG // b2Z0IENvcnBvcmF0aW9uMSYwJAYDVQQDEx1NaWNyb3Nv
// SIG // ZnQgVGltZS1TdGFtcCBQQ0EgMjAxMDAeFw0yNTAxMzAx
// SIG // OTQyNDlaFw0yNjA0MjIxOTQyNDlaMIHLMQswCQYDVQQG
// SIG // EwJVUzETMBEGA1UECBMKV2FzaGluZ3RvbjEQMA4GA1UE
// SIG // BxMHUmVkbW9uZDEeMBwGA1UEChMVTWljcm9zb2Z0IENv
// SIG // cnBvcmF0aW9uMSUwIwYDVQQLExxNaWNyb3NvZnQgQW1l
// SIG // cmljYSBPcGVyYXRpb25zMScwJQYDVQQLEx5uU2hpZWxk
// SIG // IFRTUyBFU046RjAwMi0wNUUwLUQ5NDcxJTAjBgNVBAMT
// SIG // HE1pY3Jvc29mdCBUaW1lLVN0YW1wIFNlcnZpY2UwggIi
// SIG // MA0GCSqGSIb3DQEBAQUAA4ICDwAwggIKAoICAQCSkvLf
// SIG // d7gF1r2wGdy85CFYXHUC8ywEyD4LRLv0WYEXeeZ0u5Yu
// SIG // K7p2cXVzQmZPOHTN8TWqG2SPlUb+7PldzFDDAlR3vU8p
// SIG // iOjmhu9rHW43M2dbor9jl9gluhzwUd2SciVGa7f9t67t
// SIG // M3KFKRSMXFtHKF3KwBB7aVo+b1qy5p9DWlo2N5FGrBqH
// SIG // MEVlNyzreHYoDLL+m8fSsqMu/iYUqxzK5F4S7IY5NemA
// SIG // B8B+A3QgwVIi64KJIfeKZUeiWKCTf4odUgP3AQilxh48
// SIG // P6z7AT4IA0dMEtKhYLFs4W/KNDMsYr7KpQPKVCcC5E8u
// SIG // DHdKewubyzenkTxy4ff1N3g8yho5Pi9BfjR0VytrkmpD
// SIG // fep8JPwcb4BNOIXOo1pfdHZ8EvnR7JFZFQiqpMZFlO5C
// SIG // AuTYH8ujc5PUHlaMAJ8NEa9TFJTOSBrB7PRgeh/6NJ2x
// SIG // u9yxPh/kVN9BGss93MC6UjpoxeM4x70bwbwiK8SNHIO8
// SIG // D8cql7VSevUYbjN4NogFFwhBClhodE/zeGPq6y6ixD4z
// SIG // 65IHY3zwFQbBVX/w+L/VHNn/BMGs2PGHnlRjO/Kk8NIp
// SIG // N4shkFQqA1fM08frrDSNEY9VKDtpsUpAF51Y1oQ6tJhW
// SIG // M1d3neCXh6b/6N+XeHORCwnY83K+pFMMhg8isXQb6KRl
// SIG // 65kg8XYBd4JwkbKoVQIDAQABo4IBSTCCAUUwHQYDVR0O
// SIG // BBYEFHR6Wrs27b6+yJ3bEZ9o5NdL1bLwMB8GA1UdIwQY
// SIG // MBaAFJ+nFV0AXmJdg/Tl0mWnG1M1GelyMF8GA1UdHwRY
// SIG // MFYwVKBSoFCGTmh0dHA6Ly93d3cubWljcm9zb2Z0LmNv
// SIG // bS9wa2lvcHMvY3JsL01pY3Jvc29mdCUyMFRpbWUtU3Rh
// SIG // bXAlMjBQQ0ElMjAyMDEwKDEpLmNybDBsBggrBgEFBQcB
// SIG // AQRgMF4wXAYIKwYBBQUHMAKGUGh0dHA6Ly93d3cubWlj
// SIG // cm9zb2Z0LmNvbS9wa2lvcHMvY2VydHMvTWljcm9zb2Z0
// SIG // JTIwVGltZS1TdGFtcCUyMFBDQSUyMDIwMTAoMSkuY3J0
// SIG // MAwGA1UdEwEB/wQCMAAwFgYDVR0lAQH/BAwwCgYIKwYB
// SIG // BQUHAwgwDgYDVR0PAQH/BAQDAgeAMA0GCSqGSIb3DQEB
// SIG // CwUAA4ICAQAOuxk47b1i75V81Tx6xo10xNIr4zZxYVfk
// SIG // F5TFq2kndPHgzVyLnssw/HKkEZRCgZVpkKEJ6Y4jvG5t
// SIG // ugMi+Wjt7hUMSipk+RpB5gFQvh1xmAEL2flegzTWEsnj
// SIG // 0wrESplI5Z3vgf2eGXAr/RcqGjSpouHbD2HY9Y3F0Ol6
// SIG // FRDCV/HEGKRHzn2M5rQpFGSjacT4DkqVYmem/ArOfSvV
// SIG // ojnKEIW914UxGtuhJSr9jOo5RqTX7GIqbtvN7zhWld+i
// SIG // 3XxdhdNcflQz9YhoFqQexBenoIRgAPAtwH68xczr9LMC
// SIG // 3l9ALEpnsvO0RiKPXF4l22/OfcFffaphnl/TDwkiJfxO
// SIG // yAMfUF3xI9+3izT1WX2CFs2RaOAq3dcohyJw+xRG0E8w
// SIG // kCHqkV57BbUBEzLX8L9lGJ1DoxYNpoDX7iQzJ9Qdkypi
// SIG // 5fv773E3Ch8A+toxeFp6FifQZyCc8IcIBlHyak6MbT6Y
// SIG // TVQNgQ/h8FF+S5OqP7CECFvIH2Kt2P0GlOu9C0Bfashn
// SIG // TjodmtZFZsptUvirk/2HOLLjBiMjDwJsQAFAzJuz4ZtT
// SIG // yorrvER10Gl/mbmViHqhvNACfTzPiLfjDgyvp9s7/bHu
// SIG // /CalKmeiJULGjh/lwAj5319pggsGJqbhJ4FbFc+oU5zf
// SIG // fbm/rKjVZ8kxND3im10Qp41n2t/qpyP6ETCCB3EwggVZ
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
// SIG // gm2lBRDBcQZqELQdVTNYs6FwZvKhggNNMIICNQIBATCB
// SIG // +aGB0aSBzjCByzELMAkGA1UEBhMCVVMxEzARBgNVBAgT
// SIG // Cldhc2hpbmd0b24xEDAOBgNVBAcTB1JlZG1vbmQxHjAc
// SIG // BgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjElMCMG
// SIG // A1UECxMcTWljcm9zb2Z0IEFtZXJpY2EgT3BlcmF0aW9u
// SIG // czEnMCUGA1UECxMeblNoaWVsZCBUU1MgRVNOOkYwMDIt
// SIG // MDVFMC1EOTQ3MSUwIwYDVQQDExxNaWNyb3NvZnQgVGlt
// SIG // ZS1TdGFtcCBTZXJ2aWNloiMKAQEwBwYFKw4DAhoDFQDV
// SIG // sH9p1tJn+krwCMvqOhVvXrbetKCBgzCBgKR+MHwxCzAJ
// SIG // BgNVBAYTAlVTMRMwEQYDVQQIEwpXYXNoaW5ndG9uMRAw
// SIG // DgYDVQQHEwdSZWRtb25kMR4wHAYDVQQKExVNaWNyb3Nv
// SIG // ZnQgQ29ycG9yYXRpb24xJjAkBgNVBAMTHU1pY3Jvc29m
// SIG // dCBUaW1lLVN0YW1wIFBDQSAyMDEwMA0GCSqGSIb3DQEB
// SIG // CwUAAgUA7IooLTAiGA8yMDI1MTAwMzEwNDcwOVoYDzIw
// SIG // MjUxMDA0MTA0NzA5WjB0MDoGCisGAQQBhFkKBAExLDAq
// SIG // MAoCBQDsiigtAgEAMAcCAQACAkwEMAcCAQACAhQkMAoC
// SIG // BQDsi3mtAgEAMDYGCisGAQQBhFkKBAIxKDAmMAwGCisG
// SIG // AQQBhFkKAwKgCjAIAgEAAgMHoSChCjAIAgEAAgMBhqAw
// SIG // DQYJKoZIhvcNAQELBQADggEBAAG0LbJshL3Xr7Qlyy/I
// SIG // v4zbQ1jFjNHDZE4X2+/fSmpZAIlOMuMG/6JFaS9zz+/G
// SIG // 8I1syELZ01UvcS17SKvfwP6uYUnKiT5hw+NQWqdd4s5k
// SIG // aZ/O/dl2Xv8C5PJm5YOgtyA9bTgjtXM6X9A8R7YmtwSn
// SIG // TA/BWzsxX6B+ZNx5QaOT9C2AlEsAKGLNdobrxs34eXPj
// SIG // DslqpXxqX4wMERkvYHl0OKjfuvqTYz87CFHMw/ThwSdx
// SIG // M3cnkWGGwuGNdg1ng7IKyn+4GfyANU3gk3fojUhyI2J7
// SIG // ZU1096oLdezMa/THR50NrelyC6QGxigmkiMdM21XKemk
// SIG // qBbAWYxn3ioeXRIxggQNMIIECQIBATCBkzB8MQswCQYD
// SIG // VQQGEwJVUzETMBEGA1UECBMKV2FzaGluZ3RvbjEQMA4G
// SIG // A1UEBxMHUmVkbW9uZDEeMBwGA1UEChMVTWljcm9zb2Z0
// SIG // IENvcnBvcmF0aW9uMSYwJAYDVQQDEx1NaWNyb3NvZnQg
// SIG // VGltZS1TdGFtcCBQQ0EgMjAxMAITMwAAAgU8dWyCRIfN
// SIG // /gABAAACBTANBglghkgBZQMEAgEFAKCCAUowGgYJKoZI
// SIG // hvcNAQkDMQ0GCyqGSIb3DQEJEAEEMC8GCSqGSIb3DQEJ
// SIG // BDEiBCAcsuexJuVWk0cgjZCBuFpDJnC08O+LHrbqsWto
// SIG // wQMLpzCB+gYLKoZIhvcNAQkQAi8xgeowgecwgeQwgb0E
// SIG // IIANAz3ceY0umhdWLR2sJpq0OPqtJDTAYRmjHVkwEW9I
// SIG // MIGYMIGApH4wfDELMAkGA1UEBhMCVVMxEzARBgNVBAgT
// SIG // Cldhc2hpbmd0b24xEDAOBgNVBAcTB1JlZG1vbmQxHjAc
// SIG // BgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjEmMCQG
// SIG // A1UEAxMdTWljcm9zb2Z0IFRpbWUtU3RhbXAgUENBIDIw
// SIG // MTACEzMAAAIFPHVsgkSHzf4AAQAAAgUwIgQghHZmyT4L
// SIG // tzkqyAeAmC6KbZZ3XDM6OtS3hvGKS0loX58wDQYJKoZI
// SIG // hvcNAQELBQAEggIAVqU1Qzrcjmc0S1jjj/duiG72y2+Q
// SIG // QbrJx8e3oqourohTXBnbMnkBWCYI6dOv49cbbvpLxTzk
// SIG // Zp4EhXa41qNpv6+/sNoUkg0ljSup49tjxXkq9GvCIX8J
// SIG // wbwbXRxGcaJX0N5ZXrQstc2pv4PzvGeQGU8773HPr1al
// SIG // h0XTD+RxaBq+R6ZZthCJy1jz1zS8WG4PdG+wjY4qiMk6
// SIG // Z3WrDO1JBHf7pA6/Ndj5vwQ2AQtA9lLs4m06/61UdzTV
// SIG // 2Rv2tOrY3aA/+eNDDJV49+HEP4Gd4Tx85P9rLhEJo3o9
// SIG // GSChUScMmRJ92PCnfGpwhdIJI5GSlLJCMCJEeiS/yohJ
// SIG // I4g7/8Sa5OX6XRpUlPvIFCSv4LBXWUy15sMSz3stc2DJ
// SIG // iwtPBUiEir2czrOcCX+N/aFuDbvhnQf8AWddK/mZbQ40
// SIG // hXxboNHJ2lfHP+ruluBu/G474Wz3rr5M3PsB2wiwfvzJ
// SIG // QlzZE9duYVvSrAb201/4rDka2+BzaQ6YvNRK83s5WMco
// SIG // sFH4YezW01QnD1wQVxFxfxb18pKBepudZVZflxTHzK4w
// SIG // 2sJQQaGFPfJY0W1olmZqMEdZIL0UwqoTQm/dTG0lSs9n
// SIG // nSL9BmIrB1KJYQL7J342Ye6fVF9u9mjaLe264LVTDBqL
// SIG // igTIliqR6UL3rixxc3IENilJMgl3vgubR5F99y0=
// SIG // End signature block
