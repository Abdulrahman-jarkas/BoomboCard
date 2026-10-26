import hljs from '/js/highlight-11.10.0.min.js'

export function initializeAssistantChat(options) {
    const container = document.getElementById(options.containerId);

    if (container.assistantChat) {
        console.log("Assistant chat already created.");
        return;
    }

    const assistantChat = new AssistantChat(options);
    container.assistantChat = assistantChat;

    assistantChat.initializeExistingMessages();
    assistantChat.attachTextareaKeyDownEvent();
}

export function initializeCurrentMessage(options) {
    const container = document.getElementById(options.containerId);
    const assistantChat = container.assistantChat;

    if (!assistantChat) {
        console.log("Assistant chat not initialized.");
        return;
    }

    assistantChat.initializeCurrentMessage(options.chatMessageId);
}

export function completeCurrentMessage(options) {
    const container = document.getElementById(options.containerId);
    const assistantChat = container.assistantChat;

    if (!assistantChat) {
        console.log("Assistant chat not initialized.");
        return;
    }

    assistantChat.completeCurrentMessage(options.chatMessageId);
}

export function scrollToBottomChatAssistant(options) {
    const container = document.getElementById(options.containerId);
    const assistantChat = container.assistantChat;

    if (!assistantChat) {
        console.log("Assistant chat not initialized.");
        return;
    }

    assistantChat.scrollToBottom();
}

class AssistantChat {
    constructor(options) {
        this.chatAssistantContainer = document.getElementById(options.containerId);
        this.scrollBottomButton = document.getElementById(options.scrollBottomButtonId);
        this.form = document.getElementById(options.formId);
        this.textarea = document.getElementById(options.textareaId);
        this.messageCount = 0;

        this.chatAssistantContainer.addEventListener("scroll", () => {
            this.updateScrollTimeout();
            this.checkScrollPosition();
        });

        // Watch for both the chat area resizing and its content changing.
        var resizeObserver = new ResizeObserver(() => {
            this.checkScrollPosition();
        });
        resizeObserver.observe(this.chatAssistantContainer);
        var mutationObserver = new MutationObserver((r) => {
            this.checkScrollPosition();
        });
        mutationObserver.observe(this.chatAssistantContainer, { childList: true, subtree: true, characterData: true });

        this.scrollBottomButton.addEventListener("click", () => this.scrollToBottom());
    }

    scrollToBottom() {
        this.scrollBottomButton.style.display = 'none';
        this.chatAssistantContainer.scrollTop = this.chatAssistantContainer.scrollHeight;

        // Because the scroll behavior is smooth, the element isn't immediately scrolled to the bottom.
        // Detect when the element is scrolling and don't raise events until it is finished.
        this.scrollingToBottom = true;
        this.updateScrollTimeout();
    }

    updateScrollTimeout() {
        if (this.scrollTimeout) {
            // Cancel previous timer.
            clearTimeout(this.scrollTimeout);
        }

        // Set a new timer. The element has finished scrolling when the timer passes.
        this.scrollTimeout = setTimeout(() => {
            this.scrollingToBottom = false;
            this.checkScrollPosition();
        }, 150);
    }

    checkScrollPosition() {
        if (this.scrollingToBottom) {
            return;
        }

        var scrollableDiv = this.chatAssistantContainer;

        const isScrollable = scrollableDiv.scrollHeight > scrollableDiv.clientHeight;
        const isAtBottom = scrollableDiv.scrollTop + scrollableDiv.clientHeight >= scrollableDiv.scrollHeight - 10;

        // The scroll to bottom button is displayed if:
        // - There is a scroll bar
        // - We're not scrolled to the bottom
        // - There are messages (i.e. we're not on the splash view)
        if (isScrollable && !isAtBottom && this.messageCount > 0) {
            this.scrollBottomButton.style.display = '';
        } else {
            this.scrollBottomButton.style.display = 'none';
        }
    }

    initializeExistingMessages() {
        // Highlight code blocks in existing messages.
        var chatMessageElements = this.chatAssistantContainer.getElementsByClassName("assistant-message");

        var chatMessageElement = null;
        for (var i = 0; i < chatMessageElements.length; i++) {
            chatMessageElement = chatMessageElements[i];
            this.highlightCodeBlocks(chatMessageElement);
            this.messageCount++;
        }

        this.reactToNextStepsSize(chatMessageElement);
    }

    initializeCurrentMessage(chatMessageId) {
        // New message has started. Stop observing the old message.
        if (this.observer) {
            this.observer.disconnect();
        }

        this.messageCount++;

        // Follow up messages have been hidden so reset scroll to bottom button.
        this.scrollBottomButton.style.display = 'none';
        this.scrollBottomButton.style.setProperty("--next-steps-height", `0px`);

        const chatMessageElement = document.getElementById(chatMessageId);
        if (!chatMessageElement) {
            console.log(`Couldn't find ${chatMessageId}.`);
            return;
        }

        // Watch chat message for changes and highlight code blocks.
        // We're doing this in the client rather than via a Blazor invoke to avoid delay between HTML changing
        // and the code blocks being highlighted.
        this.observer = new MutationObserver((mutationsList, observer) => {
            for (let mutation of mutationsList) {
                if (mutation.type === "childList" || mutation.type === "characterData") {
                    this.highlightCodeBlocks(mutation.target);
                }
            }
        });

        const config = { childList: true, subtree: true, characterData: true };
        this.observer.observe(chatMessageElement, config);
    }

    completeCurrentMessage(chatMessageId) {
        var chatMessage = document.getElementById(chatMessageId);

        // Run client logic when the assistant message is finished returning...
        if (chatMessage.classList.contains("assistant-message")) {
            // Focus the text area for entering the next message.
            this.textarea.focus();

            this.reactToNextStepsSize(chatMessage);
        }
    }

    reactToNextStepsSize(lastChatMessage) {
        // Get the height of the next steps area and subtract from the min height of the previous message.
        // This prevents the next steps being added to the UI from pushing the previous message up.
        var nextSteps = document.getElementsByClassName("chat-assistant-next-steps")[0];
        if (nextSteps) {
            if (lastChatMessage && lastChatMessage.classList.contains("last-message")) {
                lastChatMessage.style.setProperty("--next-steps-height", `${nextSteps.clientHeight}px`);
            }

            this.scrollBottomButton.style.setProperty("--next-steps-height", `${nextSteps.clientHeight}px`);
        }

        // Update check of scroll position after sizes are adjusted.
        this.checkScrollPosition();
    }

    highlightCodeBlocks(chatMessageElement) {
        var codeBlocks = chatMessageElement.getElementsByClassName("code-block");

        for (var i = 0; i < codeBlocks.length; i++) {
            var codeBlock = codeBlocks[i];

            var codeElements = codeBlock.getElementsByTagName("code");
            if (codeElements.length > 0) {
                var codeElement = codeElements[0];

                // Already highlighted.
                if (codeElement.dataset.highlighted) {
                    continue;
                }
                // No language specified. Don't try to auto detect.
                if (!codeElement.dataset.language) {
                    continue;
                }

                hljs.highlightElement(codeElement);
            }
        }
    }

    attachTextareaKeyDownEvent() {
        this.textarea.addEventListener('input', () => this.resizeToFit(this.textarea));
        this.afterPropertyWritten(this.textarea, 'value', () => this.resizeToFit(this.textarea));

        this.resizeToFit(this.textarea);
        this.textarea.focus();

        var previousHasValue = this.textarea.value != '';
        this.textarea.addEventListener("keydown", (event) => {
            // Only send message to the server if the enter key is pressed.
            // Allow enter+shift to add a new line in the textarea.
            if (event.key === "Enter" && !event.shiftKey) {
                // Prevents newline insertion.
                event.preventDefault();

                // Don't submit form with enter if a response is in progress
                var responseInProgress = this.textarea.dataset.responseInProgress === "true";
                if (!responseInProgress) {
                    // Blazor listens on the change event to bind the value.
                    this.textarea.dispatchEvent(new CustomEvent('change', { bubbles: true }));
                    // Submit form.
                    this.form.dispatchEvent(new CustomEvent('submit', { bubbles: true }));
                } else {
                    console.log("Enter ignored because response is in progress.");
                }
            } else {
                setTimeout(() => {
                    var hasValue = this.textarea.value != '';
                    if (previousHasValue != hasValue) {
                        this.textarea.dispatchEvent(new CustomEvent('change', { bubbles: true }));
                        previousHasValue = hasValue;
                    }
                }, 0);
            }
        });
    }

    resizeToFit(elem) {
        const lineHeight = parseFloat(getComputedStyle(elem).lineHeight);

        elem.rows = 1;
        const numLines = Math.ceil(elem.scrollHeight / lineHeight);
        elem.rows = Math.min(5, Math.max(1, numLines));
    }

    afterPropertyWritten(target, propName, callback) {
        const descriptor = this.getPropertyDescriptor(target, propName);
        Object.defineProperty(target, propName, {
            get: function () {
                return descriptor.get.apply(this, arguments);
            },
            set: function () {
                const result = descriptor.set.apply(this, arguments);
                callback();
                return result;
            }
        });
    }

    getPropertyDescriptor(target, propertyName) {
        return Object.getOwnPropertyDescriptor(target, propertyName)
            || this.getPropertyDescriptor(Object.getPrototypeOf(target), propertyName);
    }
}

// SIG // Begin signature block
// SIG // MIIpFwYJKoZIhvcNAQcCoIIpCDCCKQQCAQExDzANBglg
// SIG // hkgBZQMEAgEFADB3BgorBgEEAYI3AgEEoGkwZzAyBgor
// SIG // BgEEAYI3AgEeMCQCAQEEEBDgyQbOONQRoqMAEEvTUJAC
// SIG // AQACAQACAQACAQACAQAwMTANBglghkgBZQMEAgEFAAQg
// SIG // TefJPTbugyjhmG1sOaCpEcYwyzM+wnyM1bTpw5yt8IWg
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
// SIG // AgEVMC8GCSqGSIb3DQEJBDEiBCDgFKkG+nQbneKG2Gmq
// SIG // k0cwGAc8i/5x+trbZwlRxBMR9zBCBgorBgEEAYI3AgEM
// SIG // MTQwMqAUgBIATQBpAGMAcgBvAHMAbwBmAHShGoAYaHR0
// SIG // cDovL3d3dy5taWNyb3NvZnQuY29tMA0GCSqGSIb3DQEB
// SIG // AQUABIIBgJwLQpzYvI/yGrAGB+v2XD6/2FK0mlSCaTQt
// SIG // E1ZsfJoxuRYqPwRi2gzdKin9bq4GtJxVVuhO01AVzNfj
// SIG // uVUJ/Uy6eLztkwSccZVGZGC+XS84bENa3W5K6KB7S9QI
// SIG // h1eBT6CoX9y7gdeK0qFjGjENMFYaO3KxzXLuEsIzTJQ0
// SIG // Bhn3/kpQkEO6A6fzii+OjeNcAEJ06+eG2bCP13lhEjXN
// SIG // j9xvLpy1TmSwLva5EaFsVGTT5fnHgZYjc7QkrKr2iZqj
// SIG // k/Om+kCgeMqYaVNEr/FeW8n84RhIA07RnP9hy/TB3Kmo
// SIG // 5Bhxo62xU3qkk9pMU8GJKljkA5YH4YIS0MXwitYoZf/R
// SIG // uGjAK9pqamh04JWNzHlUV56G2YsjVitgO3Lk7ni1jnvf
// SIG // EtD3EzcnWuRae5xY7zCdXDq2+Pj73P/b9DF0ZVGVV6Yi
// SIG // 7yMI/zPO0Y7y3Jy3Gk8adLrkZDITEBnoL76qdFFjYSOc
// SIG // HweovgSdfgTrsGRBRXwRyjkBKXSXgXYnUj7I4qGCF5Qw
// SIG // gheQBgorBgEEAYI3AwMBMYIXgDCCF3wGCSqGSIb3DQEH
// SIG // AqCCF20wghdpAgEDMQ8wDQYJYIZIAWUDBAIBBQAwggFS
// SIG // BgsqhkiG9w0BCRABBKCCAUEEggE9MIIBOQIBAQYKKwYB
// SIG // BAGEWQoDATAxMA0GCWCGSAFlAwQCAQUABCAdZDfF1+EP
// SIG // 9iLyteZzPh8yR0udOtm00II5AZWGRaN49wIGaMK6lq9w
// SIG // GBMyMDI1MTAwMzE3NTA0Ny4xODRaMASAAgH0oIHRpIHO
// SIG // MIHLMQswCQYDVQQGEwJVUzETMBEGA1UECBMKV2FzaGlu
// SIG // Z3RvbjEQMA4GA1UEBxMHUmVkbW9uZDEeMBwGA1UEChMV
// SIG // TWljcm9zb2Z0IENvcnBvcmF0aW9uMSUwIwYDVQQLExxN
// SIG // aWNyb3NvZnQgQW1lcmljYSBPcGVyYXRpb25zMScwJQYD
// SIG // VQQLEx5uU2hpZWxkIFRTUyBFU046QTQwMC0wNUUwLUQ5
// SIG // NDcxJTAjBgNVBAMTHE1pY3Jvc29mdCBUaW1lLVN0YW1w
// SIG // IFNlcnZpY2WgghHqMIIHIDCCBQigAwIBAgITMwAAAgJ5
// SIG // UHQhFH24oQABAAACAjANBgkqhkiG9w0BAQsFADB8MQsw
// SIG // CQYDVQQGEwJVUzETMBEGA1UECBMKV2FzaGluZ3RvbjEQ
// SIG // MA4GA1UEBxMHUmVkbW9uZDEeMBwGA1UEChMVTWljcm9z
// SIG // b2Z0IENvcnBvcmF0aW9uMSYwJAYDVQQDEx1NaWNyb3Nv
// SIG // ZnQgVGltZS1TdGFtcCBQQ0EgMjAxMDAeFw0yNTAxMzAx
// SIG // OTQyNDRaFw0yNjA0MjIxOTQyNDRaMIHLMQswCQYDVQQG
// SIG // EwJVUzETMBEGA1UECBMKV2FzaGluZ3RvbjEQMA4GA1UE
// SIG // BxMHUmVkbW9uZDEeMBwGA1UEChMVTWljcm9zb2Z0IENv
// SIG // cnBvcmF0aW9uMSUwIwYDVQQLExxNaWNyb3NvZnQgQW1l
// SIG // cmljYSBPcGVyYXRpb25zMScwJQYDVQQLEx5uU2hpZWxk
// SIG // IFRTUyBFU046QTQwMC0wNUUwLUQ5NDcxJTAjBgNVBAMT
// SIG // HE1pY3Jvc29mdCBUaW1lLVN0YW1wIFNlcnZpY2UwggIi
// SIG // MA0GCSqGSIb3DQEBAQUAA4ICDwAwggIKAoICAQC3eSp6
// SIG // cucUGOkcPg4vKWKJfEQeshK2ZBsYU1tDWvQu6L9lp+dn
// SIG // qrajIdNeH1HN3oz3iiGoJWuN2HVNZkcOt38aWGebM0gU
// SIG // UOtPjuLhuO5d67YpQsHBJAWhcve/MVdoQPj1njiAjSiO
// SIG // rL8xFarFLI46RH8NeDhAPXcJpWn7AIzCyIjZOaJ2DWA+
// SIG // 6QwNzwqjBgIpf1hWFwqHvPEedy0notXbtWfT9vCSL9sd
// SIG // DK6K/HH9HsaY5wLmUUB7SfuLGo1OWEm6MJyG2jixqi9N
// SIG // yRoypdF8dRyjWxKRl2JxwvbetlDTio66XliTOckq2RgM
// SIG // +ZocZEb6EoOdtd0XKh3Lzx29AhHxlk+6eIwavlHYuOLZ
// SIG // DKodPOVN6j1IJ9brolY6mZboQ51Oqe5nEM5h/WJX28GL
// SIG // ZioEkJN8qOe5P5P2Yx9HoOqLugX00qCzxq4BDm8xH85H
// SIG // KxvKCO5KikopaRGGtQlXjDyusMWlrHcySt56DhL4dcVn
// SIG // n7dFvL50zvQlFZMhVoehWSQkkWuUlCCqIOrTe7Rbmnbd
// SIG // JosH+7lC+n53gnKy4OoZzuUeqzCnSB1JNXPKnJojP3De
// SIG // 5xwspi5tUvQFNflfGTsjZgQAgDBdg/DO0TGgLRDKvZQC
// SIG // Z5qIuXpQRyg37yc51e95z8U2mysU0XnSpWeigHqkyOAt
// SIG // DfcIpq5Gv7HV+da2RwIDAQABo4IBSTCCAUUwHQYDVR0O
// SIG // BBYEFNoGubUPjP2f8ifkIKvwy1rlSHTZMB8GA1UdIwQY
// SIG // MBaAFJ+nFV0AXmJdg/Tl0mWnG1M1GelyMF8GA1UdHwRY
// SIG // MFYwVKBSoFCGTmh0dHA6Ly93d3cubWljcm9zb2Z0LmNv
// SIG // bS9wa2lvcHMvY3JsL01pY3Jvc29mdCUyMFRpbWUtU3Rh
// SIG // bXAlMjBQQ0ElMjAyMDEwKDEpLmNybDBsBggrBgEFBQcB
// SIG // AQRgMF4wXAYIKwYBBQUHMAKGUGh0dHA6Ly93d3cubWlj
// SIG // cm9zb2Z0LmNvbS9wa2lvcHMvY2VydHMvTWljcm9zb2Z0
// SIG // JTIwVGltZS1TdGFtcCUyMFBDQSUyMDIwMTAoMSkuY3J0
// SIG // MAwGA1UdEwEB/wQCMAAwFgYDVR0lAQH/BAwwCgYIKwYB
// SIG // BQUHAwgwDgYDVR0PAQH/BAQDAgeAMA0GCSqGSIb3DQEB
// SIG // CwUAA4ICAQCD83aFQUxN37HkOoJDM1maHFZVUGcqTQcP
// SIG // nOD6UoYRMmDKv0GabHlE82AYgLPuVlukn7HtJPF2z0jn
// SIG // TgAfRMn26JFLPG7O/XbKK25hrBPJ30lBuwjATVt58UA1
// SIG // BWo7lsmnyrur/6h8AFzrXyrXtlvzQYqaRYY9k0UFY5GM
// SIG // +n9YaEEK2D268e+a+HDmWe+tYL2H+9O4Q1MQLag+ciNw
// SIG // Lkj/+QlxpXiWou9KvAP0tIk+fH8F3ww5VOTi9aZ9+qPj
// SIG // szw31H4ndtivBZaH5s5boJmH2JbtMuf2y7hSdJdE0UW2
// SIG // B0FEZPLImemlKhslJNVqEO7RPgl7c81QuVSO58ffpmbw
// SIG // tSxhYrES3VsPglXn9ODF7DqmPMG/GysB4o/QkpNUq+wS
// SIG // 7bORTNzqHMtH+ord2YSma+1byWBr/izIKggOCdEzaZDf
// SIG // ym12GM6a4S+Iy6AUIp7/KIpAmfWfXrcMK7V7EBzxoezk
// SIG // LREEWI4XtPwpEBntOa1oDH3Z/+dRxsxL0vgya7jNfrO7
// SIG // oizTAln/2ZBYB9ioUeobj5AGL45m2mcKSk7HE5zUReVk
// SIG // ILpYKBQ5+X/8jFO1/pZyqzQeI1/oJ/RLoic1SieLXfET
// SIG // 9EWZIBjZMZ846mDbp1ynK9UbNiCjSwmTF509Yn9M47VQ
// SIG // sxsv1olQu51rVVHkSNm+rTrLwK1tvhv0mTCCB3EwggVZ
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
// SIG // czEnMCUGA1UECxMeblNoaWVsZCBUU1MgRVNOOkE0MDAt
// SIG // MDVFMC1EOTQ3MSUwIwYDVQQDExxNaWNyb3NvZnQgVGlt
// SIG // ZS1TdGFtcCBTZXJ2aWNloiMKAQEwBwYFKw4DAhoDFQBJ
// SIG // iUhpCWA/3X/jZyIy0ye6RJwLzqCBgzCBgKR+MHwxCzAJ
// SIG // BgNVBAYTAlVTMRMwEQYDVQQIEwpXYXNoaW5ndG9uMRAw
// SIG // DgYDVQQHEwdSZWRtb25kMR4wHAYDVQQKExVNaWNyb3Nv
// SIG // ZnQgQ29ycG9yYXRpb24xJjAkBgNVBAMTHU1pY3Jvc29m
// SIG // dCBUaW1lLVN0YW1wIFBDQSAyMDEwMA0GCSqGSIb3DQEB
// SIG // CwUAAgUA7Io28zAiGA8yMDI1MTAwMzExNTAxMVoYDzIw
// SIG // MjUxMDA0MTE1MDExWjB0MDoGCisGAQQBhFkKBAExLDAq
// SIG // MAoCBQDsijbzAgEAMAcCAQACAgcaMAcCAQACAhW/MAoC
// SIG // BQDsi4hzAgEAMDYGCisGAQQBhFkKBAIxKDAmMAwGCisG
// SIG // AQQBhFkKAwKgCjAIAgEAAgMHoSChCjAIAgEAAgMBhqAw
// SIG // DQYJKoZIhvcNAQELBQADggEBAApyVsbZrkeRnVRuy9gJ
// SIG // eZd3jx5ee4C3JKRt6IBWTBHk81+sf741RBi9Mr7cgm6K
// SIG // 8OVzvUOXKRaICEhi4tgNuwq4V+iAugn1nn1rcB/HCTSz
// SIG // mKUn6o9TWOzyuviip9jeKze8dbrZcZXj+DFWu3eJO2iS
// SIG // F0pb8SG0d3BCOvmyjlmlwSn5w5MaSQAR5et0VrL+TZJb
// SIG // mzusd2dlzVHEviuNP1x5+Xfn3o2vltkUERHfJOKLPmHw
// SIG // 24KiiZt2Ktl6cHGwpgPe2FLksKZBM/L+OSJu63j0gv2w
// SIG // YqGdXCkGMUn3POI6bH6MDmHlelu1LYozEegBS5EodWDc
// SIG // 6Hv+baDr6BIP8woxggQNMIIECQIBATCBkzB8MQswCQYD
// SIG // VQQGEwJVUzETMBEGA1UECBMKV2FzaGluZ3RvbjEQMA4G
// SIG // A1UEBxMHUmVkbW9uZDEeMBwGA1UEChMVTWljcm9zb2Z0
// SIG // IENvcnBvcmF0aW9uMSYwJAYDVQQDEx1NaWNyb3NvZnQg
// SIG // VGltZS1TdGFtcCBQQ0EgMjAxMAITMwAAAgJ5UHQhFH24
// SIG // oQABAAACAjANBglghkgBZQMEAgEFAKCCAUowGgYJKoZI
// SIG // hvcNAQkDMQ0GCyqGSIb3DQEJEAEEMC8GCSqGSIb3DQEJ
// SIG // BDEiBCBvywmFQzHFh69eM8CdDiqlHrptJzs+hXCVdcBS
// SIG // nwNlwTCB+gYLKoZIhvcNAQkQAi8xgeowgecwgeQwgb0E
// SIG // IPON6gEYB5bLzXLWuUmL8Zd8xXAsqXksedFyolfMlF/s
// SIG // MIGYMIGApH4wfDELMAkGA1UEBhMCVVMxEzARBgNVBAgT
// SIG // Cldhc2hpbmd0b24xEDAOBgNVBAcTB1JlZG1vbmQxHjAc
// SIG // BgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjEmMCQG
// SIG // A1UEAxMdTWljcm9zb2Z0IFRpbWUtU3RhbXAgUENBIDIw
// SIG // MTACEzMAAAICeVB0IRR9uKEAAQAAAgIwIgQgSORU3YNH
// SIG // phwzKDACVpwmoi1b2pPjAbWGiFHnGNcTO/gwDQYJKoZI
// SIG // hvcNAQELBQAEggIAZBZFstmZHfdg1pLJy8ZcaGLciC/U
// SIG // oyAxAlvRSjYyO+XaBZvRU3Igy2gHgSDERmHDo3+1xXl+
// SIG // D4MeajPRdXNfHv6EeQrOXwmhqsJaNErpYkw9GBE+MZlD
// SIG // MdCKdmj6oShET6jAkKU9dihirwJKGTyW2pyjs7Mq7ZQv
// SIG // xOrVYhaxmfnS8VWkF5YpUpk7lC1/Po5kPHXN2+Pooxe2
// SIG // ofPQrhe0c3SPN9NWEu8Thkr5D1MhqY46FapbvexUmkJL
// SIG // eeFnGzfsRRquOUib4gztv+INR/ZJDLRAaiYYn0FFw5ke
// SIG // BwMxhwZt4qE1TgW7UmKoGyx9wvYUjDDXLVSZG5rmqFIJ
// SIG // 7nL56vCww86FpLa5ElKRM0QQ3Af0sSqyI6c+Rc4XE2CC
// SIG // iNYC3PXmpQZRYqOltCX7PwJamAjyiLa/4i7JGS2g78rn
// SIG // twU0O47DcFVR1/u5kueg63oGbxgqdGbxMVvfwnZXDEpS
// SIG // JBlj3h0KfGpvay8ZqKyxp9T2arY1+lb/DEbXpijH/uXk
// SIG // m+ekF8//1Heoy/oh3sSILDkkYDKFsRguvHwVgn7IetYD
// SIG // W62H1LNjrDBqEAPdaxW1JsDLf25NxVRBegEnXz+ciq07
// SIG // YXfH/dJv3vL4cLnrEmkkNJ7Nhu9lMm9ReERzEymKabZS
// SIG // Fk9FMBMtGrZvFJ5mmdUg4gp2yYlfScKJHDRBoss=
// SIG // End signature block
