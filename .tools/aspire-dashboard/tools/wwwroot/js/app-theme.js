import {
    accentBaseColor,
    baseLayerLuminance,
    SwatchRGB,
    fillColor,
    neutralLayerL2,
    neutralPalette,
    DesignToken,
    neutralFillLayerRestDelta
} from "/_content/Microsoft.FluentUI.AspNetCore.Components/Microsoft.FluentUI.AspNetCore.Components.lib.module.js";

const currentThemeCookieName = "currentTheme";
const themeSettingDark = "Dark";
const themeSettingLight = "Light";
const darkThemeLuminance = 0.19;
const lightThemeLuminance = 1.0;
const darknessLuminanceTarget = (-0.1 + Math.sqrt(0.21)) / 2;

/**
 * Updates the current theme on the site based on the specified theme
 * @param {string} specifiedTheme
 */
export function updateTheme(specifiedTheme) {
    const effectiveTheme = getEffectiveTheme(specifiedTheme);

    applyTheme(effectiveTheme);
    setThemeCookie(specifiedTheme);

    return effectiveTheme;
}

/**
 * Returns the value of the currentTheme cookie.
 * @returns {string}
 */
export function getThemeCookieValue() {
    return getCookieValue(currentThemeCookieName);
}

export function getCurrentTheme() {
    return getEffectiveTheme(getThemeCookieValue());
}

/**
 * Returns the current system theme (Light or Dark)
 * @returns {string}
 */
function getSystemTheme() {
    let matched = window.matchMedia('(prefers-color-scheme: dark)').matches;

    if (matched) {
        return themeSettingDark;
    } else {
        return themeSettingLight;
    }
}

/**
 * Sets the currentTheme cookie to the specified value.
 * @param {string} theme
 */
function setThemeCookie(theme) {
    if (theme == themeSettingDark || theme == themeSettingLight) {
        // Cookie will expire after 1 year. Using a much larger value won't have an impact because
        // Chrome limits expiration to 400 days: https://developer.chrome.com/blog/cookie-max-age-expires
        // The cookie is reset when the dashboard loads to creating a sliding expiration.
        document.cookie = `${currentThemeCookieName}=${theme}; Path=/; expires=${new Date(new Date().getTime() + 1000 * 60 * 60 * 24 * 365).toGMTString()}`;
    } else {
        // Delete cookie for other values (e.g. System)
        document.cookie = `${currentThemeCookieName}=; Path=/; expires=Thu, 01 Jan 1970 00:00:00 UTC;`;
    }
}

/**
 * Sets the document data-theme attribute to the specified value.
 * @param {string} theme The theme to set. Should be Light or Dark.
 */
function setThemeOnDocument(theme) {

    if (theme === themeSettingDark) {
        document.documentElement.setAttribute('data-theme', 'dark');
    } else /* Light */ {
        document.documentElement.setAttribute('data-theme', 'light');
    }
}

/**
 *
 * @param {string} theme The theme to use. Should be Light or Dark.
 */
function setBaseLayerLuminance(theme) {
    const baseLayerLuminanceValue = getBaseLayerLuminanceForTheme(theme);
    baseLayerLuminance.withDefault(baseLayerLuminanceValue);
}

/**
 * Returns the value of the specified cookie, or the empty string if the cookie is not present
 * @param {string} cookieName
 * @returns {string}
 */
function getCookieValue(cookieName) {
    const cookiePieces = document.cookie.split(';');
    for (let index = 0; index < cookiePieces.length; index++) {
        if (cookiePieces[index].trim().startsWith(cookieName)) {
            const cookieKeyValue = cookiePieces[index].split('=');
            if (cookieKeyValue.length > 1) {
                return cookieKeyValue[1];
            }
        }
    }

    return "";
}

/**
 * Converts a setting value for the theme (Light, Dark, System or null/empty) into the effective theme that should be applied
 * @param {string} specifiedTheme The setting value to use to determine the effective theme. Anything other than Light or Dark will be treated as System
 * @returns {string} The actual theme to use based on the supplied setting. Will be either Light or Dark.
 */
function getEffectiveTheme(specifiedTheme) {
    if (specifiedTheme === themeSettingLight ||
        specifiedTheme === themeSettingDark) {
        return specifiedTheme;
    } else {
        return getSystemTheme();
    }
}

/**
 *
 * @param {string} theme The theme to use. Should be Light or Dark
 * @returns {string}
 */
function getBaseLayerLuminanceForTheme(theme) {
    if (theme === themeSettingDark) {
        return darkThemeLuminance;
    } else /* Light */ {
        return lightThemeLuminance;
    }
}

/**
 * Configures the accent color palette based on the .NET purple
 */
function setAccentColor() {
    // Convert the base color ourselves to avoid pulling in the
    // @microsoft/fast-colors library just for one call to parseColorHexRGB
    const baseColor = { // #512BD4
        r: 0x51 / 255.0,
        g: 0x2B / 255.0,
        b: 0xD4 / 255.0
    };

    const accentBase = SwatchRGB.create(baseColor.r, baseColor.g, baseColor.b);
    accentBaseColor.withDefault(accentBase);
}

/**
 * Configures the default background color to use for the body
 */
function setFillColor() {
    // Design specs say we should use --neutral-layer-2 as the fill color
    // for the body. Most of the web components use --fill-color as their
    // background color, so we need to make sure they get --neutral-layer-2
    // when they request --fill-color.
    fillColor.setValueFor(document.body, neutralLayerL2);
}

/**
 * Applies the Light or Dark theme to the entire site
 * @param {string} theme The theme to use. Should be Light or Dark
 */
function applyTheme(theme) {
    setBaseLayerLuminance(theme);
    setAccentColor();
    setFillColor();
    setThemeOnDocument(theme);
}

/**
 *
 * @param {Palette} palette
 * @param {number} baseLayerLuminance
 * @returns {number}
 */
function neutralLayer1Index(palette, baseLayerLuminance) {
    return palette.closestIndexOf(SwatchRGB.create(baseLayerLuminance, baseLayerLuminance, baseLayerLuminance));
}

/**
 *
 * @param {Palette} palette
 * @param {Swatch} reference
 * @param {number} baseLayerLuminance
 * @param {number} layerDelta
 * @param {number} hoverDeltaLight
 * @param {number} hoverDeltaDark
 * @returns {Swatch}
 */
function neutralLayerHoverAlgorithm(palette, reference, baseLayerLuminance, layerDelta, hoverDeltaLight, hoverDeltaDark) {
    const baseIndex = neutralLayer1Index(palette, baseLayerLuminance);
    // Determine both the size of the delta (from the value passed in) and the direction (if the current color is dark,
    // the hover color will be a lower index (lighter); if the current color is light, the hover color will be a higher index (darker))
    const hoverDelta = isDark(reference) ? hoverDeltaDark * -1 : hoverDeltaLight;
    return palette.get(baseIndex + (layerDelta * -1) + hoverDelta);
}

/**
 *
 * @param {Swatch} color
 * @returns {boolean}
 */
function isDark(color) {
    return color.relativeLuminance <= darknessLuminanceTarget;
}

/**
 * Creates additional design tokens that are used to define the hover colors for the neutral layers
 * used in the site theme (neutral-layer-1 and neutral-layer-2, specifically). Unlike other -hover
 * variants, these are not created by the design system by default so we need to create them ourselves.
 * This is a lightly tweaked variant of other hover recipes used in the design system.
 */
function createAdditionalDesignTokens() {
    const neutralLayer1HoverLightDelta = DesignToken.create({ name: 'neutral-layer-1-hover-light-delta', cssCustomPropertyName: null }).withDefault(3);
    const neutralLayer1HoverDarkDelta = DesignToken.create({ name: 'neutral-layer-1-hover-dark-delta', cssCustomPropertyName: null }).withDefault(2);
    const neutralLayer2HoverLightDelta = DesignToken.create({ name: 'neutral-layer-2-hover-light-delta', cssCustomPropertyName: null }).withDefault(2);
    const neutralLayer2HoverDarkDelta = DesignToken.create({ name: 'neutral-layer-2-hover-dark-delta', cssCustomPropertyName: null }).withDefault(2);

    const neutralLayer1HoverRecipe = DesignToken.create({ name: 'neutral-layer-1-hover-recipe', cssCustomPropertyName: null }).withDefault({
        evaluate: (element, reference) =>
            neutralLayerHoverAlgorithm(
                neutralPalette.getValueFor(element),
                reference || fillColor.getValueFor(element),
                baseLayerLuminance.getValueFor(element),
                0, // No layer delta since this is for neutral-layer-1
                neutralLayer1HoverLightDelta.getValueFor(element),
                neutralLayer1HoverDarkDelta.getValueFor(element)
            ),
    });

    const neutralLayer2HoverRecipe = DesignToken.create({ name: 'neutral-layer-2-hover-recipe', cssCustomPropertyName: null }).withDefault({
        evaluate: (element, reference) =>
            neutralLayerHoverAlgorithm(
                neutralPalette.getValueFor(element),
                reference || fillColor.getValueFor(element),
                baseLayerLuminance.getValueFor(element),
                // Use the same layer delta used by the base recipe to calculate layer 2
                neutralFillLayerRestDelta.getValueFor(element),
                neutralLayer2HoverLightDelta.getValueFor(element),
                neutralLayer2HoverDarkDelta.getValueFor(element)
            ),
    });

    // Creates the --neutral-layer-1-hover custom CSS property
    DesignToken.create('neutral-layer-1-hover').withDefault((element) =>
        neutralLayer1HoverRecipe.getValueFor(element).evaluate(element),
    );

    // Creates the --neutral-layer-2-hover custom CSS property
    DesignToken.create('neutral-layer-2-hover').withDefault((element) =>
        neutralLayer2HoverRecipe.getValueFor(element).evaluate(element),
    );
}

function initializeTheme() {
    const themeCookieValue = getThemeCookieValue();
    const effectiveTheme = getEffectiveTheme(themeCookieValue);

    applyTheme(effectiveTheme);

    // If a theme cookie has been set then set it again on page load.
    // This updates the cookie expiration date and creates a sliding expiration.
    if (themeCookieValue) {
        setThemeCookie(themeCookieValue);
    }
}

createAdditionalDesignTokens();
initializeTheme();

// SIG // Begin signature block
// SIG // MIIpFwYJKoZIhvcNAQcCoIIpCDCCKQQCAQExDzANBglg
// SIG // hkgBZQMEAgEFADB3BgorBgEEAYI3AgEEoGkwZzAyBgor
// SIG // BgEEAYI3AgEeMCQCAQEEEBDgyQbOONQRoqMAEEvTUJAC
// SIG // AQACAQACAQACAQACAQAwMTANBglghkgBZQMEAgEFAAQg
// SIG // H62Soy6hZVgzSGaIDoEyXA7yxRSCf/pqy3RBS9anIlyg
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
// SIG // AgEVMC8GCSqGSIb3DQEJBDEiBCAqPHR8kNOSbEPOJVRr
// SIG // u6pXhXXxJhuWp3V2+nwuybEkZDBCBgorBgEEAYI3AgEM
// SIG // MTQwMqAUgBIATQBpAGMAcgBvAHMAbwBmAHShGoAYaHR0
// SIG // cDovL3d3dy5taWNyb3NvZnQuY29tMA0GCSqGSIb3DQEB
// SIG // AQUABIIBgKi6wbveWm1KOX4EPAktRLZmW0Mh2LbwQiLO
// SIG // vmPHIiRo3aFJlU4DX/6IJQpWxp1dq5z3piZ/Ji2k8T2J
// SIG // jDEySJpae7JQqiAeN0G2VMlvHF24rSpuSV1fZZLSAekS
// SIG // k1dzwQb2F7n6y13Dm4AFbb72BtmWnbQODWSysLU8dSaY
// SIG // s617PbNKNYWzsL+1Kvia3OPViKPdvjAXTrayBia4NB+Z
// SIG // LBqbmLSy+VmrE7CVM2csVP/45EnDQIR7QbL3yUoJuTOY
// SIG // DIZ2cvupX6zBDIUcbpwxoK3S4+8iOWFQkP3ESQKlL73J
// SIG // rbmacwU7mlTGbaom1w8JMl1QLjVd5T1lropYzXLSrHoT
// SIG // PHVKHbiTzfs7j/EP6MpV0R/2dAe+7oVHwdWFM0vXXZdf
// SIG // 6+oHg3HWJRFjCn2jh4fPJVfYRtXiHa8LDfYqGtiTKNwq
// SIG // gQFRRSoJuIk2m5958ZY5lZ0vWg4zVe5grMRRKxhYKnQ5
// SIG // Qa2506OiP9V4SGg3o+kVMhZX5rKJvslSTsLX0qGCF5Qw
// SIG // gheQBgorBgEEAYI3AwMBMYIXgDCCF3wGCSqGSIb3DQEH
// SIG // AqCCF20wghdpAgEDMQ8wDQYJYIZIAWUDBAIBBQAwggFS
// SIG // BgsqhkiG9w0BCRABBKCCAUEEggE9MIIBOQIBAQYKKwYB
// SIG // BAGEWQoDATAxMA0GCWCGSAFlAwQCAQUABCA2tHHSH5xr
// SIG // JICRZa8D1hty7MiIenWq/WobTy68DS33EgIGaKOjvA6/
// SIG // GBMyMDI1MTAwMzE3NDk0OS42ODdaMASAAgH0oIHRpIHO
// SIG // MIHLMQswCQYDVQQGEwJVUzETMBEGA1UECBMKV2FzaGlu
// SIG // Z3RvbjEQMA4GA1UEBxMHUmVkbW9uZDEeMBwGA1UEChMV
// SIG // TWljcm9zb2Z0IENvcnBvcmF0aW9uMSUwIwYDVQQLExxN
// SIG // aWNyb3NvZnQgQW1lcmljYSBPcGVyYXRpb25zMScwJQYD
// SIG // VQQLEx5uU2hpZWxkIFRTUyBFU046N0YwMC0wNUUwLUQ5
// SIG // NDcxJTAjBgNVBAMTHE1pY3Jvc29mdCBUaW1lLVN0YW1w
// SIG // IFNlcnZpY2WgghHqMIIHIDCCBQigAwIBAgITMwAAAgbX
// SIG // vFE4mCPsLAABAAACBjANBgkqhkiG9w0BAQsFADB8MQsw
// SIG // CQYDVQQGEwJVUzETMBEGA1UECBMKV2FzaGluZ3RvbjEQ
// SIG // MA4GA1UEBxMHUmVkbW9uZDEeMBwGA1UEChMVTWljcm9z
// SIG // b2Z0IENvcnBvcmF0aW9uMSYwJAYDVQQDEx1NaWNyb3Nv
// SIG // ZnQgVGltZS1TdGFtcCBQQ0EgMjAxMDAeFw0yNTAxMzAx
// SIG // OTQyNTBaFw0yNjA0MjIxOTQyNTBaMIHLMQswCQYDVQQG
// SIG // EwJVUzETMBEGA1UECBMKV2FzaGluZ3RvbjEQMA4GA1UE
// SIG // BxMHUmVkbW9uZDEeMBwGA1UEChMVTWljcm9zb2Z0IENv
// SIG // cnBvcmF0aW9uMSUwIwYDVQQLExxNaWNyb3NvZnQgQW1l
// SIG // cmljYSBPcGVyYXRpb25zMScwJQYDVQQLEx5uU2hpZWxk
// SIG // IFRTUyBFU046N0YwMC0wNUUwLUQ5NDcxJTAjBgNVBAMT
// SIG // HE1pY3Jvc29mdCBUaW1lLVN0YW1wIFNlcnZpY2UwggIi
// SIG // MA0GCSqGSIb3DQEBAQUAA4ICDwAwggIKAoICAQDpRIWb
// SIG // IM3Rlr397cjHaYx85l7I+ZVWGMCBCM911BpU6+IGWCqk
// SIG // sqgqefZFEjKzNVDYC9YcgITAz276NGgvECm4ZfNv/FPw
// SIG // caSDz7xbDbsOoxbwQoHUNRro+x5ubZhT6WJeU97F06+v
// SIG // DjAw/Yt1vWOgRTqmP/dNr9oqIbE5oCLYdH3wI/noYmsJ
// SIG // Vc7966n+B7UAGAWU2se3Lz+xdxnNsNX4CR6zIMVJTSez
// SIG // P/2STNcxJTu9k2sl7/vzOhxJhCQ38rdaEoqhGHrXrmVk
// SIG // EhSv+S00DMJc1OIXxqfbwPjMqEVp7K3kmczCkbum1BOI
// SIG // J2wuDAbKuJelpteNZj/S58NSQw6khfuJAluqHK3igkS/
// SIG // Oux49qTP+rU+PQeNuD+GtrCopFucRmanQvxISGNoxnBq
// SIG // 3UeDTqphm6aI7GMHtFD6DOjJlllH1gVWXPTyivf+4tN8
// SIG // TmO6yIgB4uP00bH9jn/dyyxSjxPQ2nGvZtgtqnvq3h3T
// SIG // RjRnkc+e1XB1uatDa1zUcS7r3iodTpyATe2hgkVX3m4D
// SIG // hRzI6A4SJ6fbJM9isLH8AGKcymisKzYupAeFSTJ10JEF
// SIG // a6MjHQYYohoCF77R0CCwMNjvE4XfLHu+qKPY8GQfsZdi
// SIG // gQ9clUAiydFmVt61hytoxZP7LmXbzjD0VecyzZoL4Equ
// SIG // 1XszBsulAr5Ld2KwcwIDAQABo4IBSTCCAUUwHQYDVR0O
// SIG // BBYEFO0wsLKdDGpT97cx3Iymyo/SBm4SMB8GA1UdIwQY
// SIG // MBaAFJ+nFV0AXmJdg/Tl0mWnG1M1GelyMF8GA1UdHwRY
// SIG // MFYwVKBSoFCGTmh0dHA6Ly93d3cubWljcm9zb2Z0LmNv
// SIG // bS9wa2lvcHMvY3JsL01pY3Jvc29mdCUyMFRpbWUtU3Rh
// SIG // bXAlMjBQQ0ElMjAyMDEwKDEpLmNybDBsBggrBgEFBQcB
// SIG // AQRgMF4wXAYIKwYBBQUHMAKGUGh0dHA6Ly93d3cubWlj
// SIG // cm9zb2Z0LmNvbS9wa2lvcHMvY2VydHMvTWljcm9zb2Z0
// SIG // JTIwVGltZS1TdGFtcCUyMFBDQSUyMDIwMTAoMSkuY3J0
// SIG // MAwGA1UdEwEB/wQCMAAwFgYDVR0lAQH/BAwwCgYIKwYB
// SIG // BQUHAwgwDgYDVR0PAQH/BAQDAgeAMA0GCSqGSIb3DQEB
// SIG // CwUAA4ICAQB23GZOfe9ThTUvD29i4t6lDpxJhpVRMme+
// SIG // UbyZhBFCZhoGTtjDdphAArU2Q61WYg3YVcl2RdJm5PUb
// SIG // Z2bA77zk+qtLxC+3dNxVsTcdtxPDSSWgwBHxTj6pCmoD
// SIG // NXolAYsWpvHQFCHDqEfAiBxX1dmaXbiTP1d0XffvgR6d
// SIG // shUcqaH/mFfjDZAxLU1s6HcVgCvBQJlJ7xEG5jFKdtqa
// SIG // pKWcbUHwTVqXQGbIlHVClNJ3yqW6Z3UJH/CFcYiLV/e6
// SIG // 8urTmGtiZxGSYb4SBSPArTrTYeHOlQIj/7loVWmfWX2y
// SIG // 4AGV/D+MzyZMyvFw4VyL0Vgq96EzQKyteiVeBaVEjxQK
// SIG // o3AcPULRF4Uzz98P2tCM5XbFZ3Qoj9PLg3rgFXr0oJEh
// SIG // fh2tqUrhTJd13+i4/fek9zWicoshlwXgFu002ZWBVzAS
// SIG // EFuqED48qyulZ/2jGJBcta+Fdk2loP2K3oSj4PQQe1Mz
// SIG // zVZO52AXO42MHlhm3SHo3/RhQ+I1A0Ny+9uAehkQH6Lr
// SIG // xkrVNvZG4f0PAKMbqUcXG7xznKJ0x0HYr5ayWGbHKZRc
// SIG // ObU+/34ZpL9NrXOedVDXmSd2ylKSl/vvi1QwNJqXJl/+
// SIG // gJkQEetqmHAUFQkFtemi8MUXQG2w/RDHXXwWAjE+qIDZ
// SIG // LQ/k4z2Z216tWaR6RDKHGkweCoDtQtzkHTCCB3EwggVZ
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
// SIG // czEnMCUGA1UECxMeblNoaWVsZCBUU1MgRVNOOjdGMDAt
// SIG // MDVFMC1EOTQ3MSUwIwYDVQQDExxNaWNyb3NvZnQgVGlt
// SIG // ZS1TdGFtcCBTZXJ2aWNloiMKAQEwBwYFKw4DAhoDFQAE
// SIG // a0f118XHM/VNdqKBs4QXxNnN96CBgzCBgKR+MHwxCzAJ
// SIG // BgNVBAYTAlVTMRMwEQYDVQQIEwpXYXNoaW5ndG9uMRAw
// SIG // DgYDVQQHEwdSZWRtb25kMR4wHAYDVQQKExVNaWNyb3Nv
// SIG // ZnQgQ29ycG9yYXRpb24xJjAkBgNVBAMTHU1pY3Jvc29m
// SIG // dCBUaW1lLVN0YW1wIFBDQSAyMDEwMA0GCSqGSIb3DQEB
// SIG // CwUAAgUA7IoY7TAiGA8yMDI1MTAwMzA5NDIwNVoYDzIw
// SIG // MjUxMDA0MDk0MjA1WjB0MDoGCisGAQQBhFkKBAExLDAq
// SIG // MAoCBQDsihjtAgEAMAcCAQACAjtMMAcCAQACAhG4MAoC
// SIG // BQDsi2ptAgEAMDYGCisGAQQBhFkKBAIxKDAmMAwGCisG
// SIG // AQQBhFkKAwKgCjAIAgEAAgMHoSChCjAIAgEAAgMBhqAw
// SIG // DQYJKoZIhvcNAQELBQADggEBAE7HcD9qK2h3dkyA1yro
// SIG // fYCFe9m9LITiYaJACCeak2HcY18656rinNb5MPRrjLts
// SIG // tDuCtSoz62RM0+W+5hClrG3OfrpzDc0NHpZNN7ZBSl6w
// SIG // YPFh9qGRT5bSHYZviJ7PNzeF7zDslZa7LcvWfAUipGHJ
// SIG // 12GlMhNArQUWOpmQCOpLk0N63CdE7LGqa+J54SZF3eDd
// SIG // U0NoAj+k1JsfgMehTW2JFC25+kyOZWGe+MCohmHp+cyn
// SIG // TwTA+LA1zUjaZIkVPhVTrilzD3E4f25E+70Ol8P3tIyM
// SIG // MDZpfkMA4T33TWpILnqreGB/QZccFh/2RfBgvbsz+1nw
// SIG // izt6opHYFv+mp8cxggQNMIIECQIBATCBkzB8MQswCQYD
// SIG // VQQGEwJVUzETMBEGA1UECBMKV2FzaGluZ3RvbjEQMA4G
// SIG // A1UEBxMHUmVkbW9uZDEeMBwGA1UEChMVTWljcm9zb2Z0
// SIG // IENvcnBvcmF0aW9uMSYwJAYDVQQDEx1NaWNyb3NvZnQg
// SIG // VGltZS1TdGFtcCBQQ0EgMjAxMAITMwAAAgbXvFE4mCPs
// SIG // LAABAAACBjANBglghkgBZQMEAgEFAKCCAUowGgYJKoZI
// SIG // hvcNAQkDMQ0GCyqGSIb3DQEJEAEEMC8GCSqGSIb3DQEJ
// SIG // BDEiBCD/ifXDWKftyhY6J+euCtVHvp6G3Tf/lk/9UXuO
// SIG // swXzBDCB+gYLKoZIhvcNAQkQAi8xgeowgecwgeQwgb0E
// SIG // IODo9ZSIkZ6dVtKT+E/uZx2WAy7KiXM5R1JIOhNJf0vS
// SIG // MIGYMIGApH4wfDELMAkGA1UEBhMCVVMxEzARBgNVBAgT
// SIG // Cldhc2hpbmd0b24xEDAOBgNVBAcTB1JlZG1vbmQxHjAc
// SIG // BgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjEmMCQG
// SIG // A1UEAxMdTWljcm9zb2Z0IFRpbWUtU3RhbXAgUENBIDIw
// SIG // MTACEzMAAAIG17xROJgj7CwAAQAAAgYwIgQghuohIC45
// SIG // mor6WnFWC9TAlvrpWqzGdUbbTURbHP36mpQwDQYJKoZI
// SIG // hvcNAQELBQAEggIAoBf3oYcnwzihFhZGbrbgSm8pY6V3
// SIG // NV/YgGy2x3BcxYy3Q1Av9zjg0lfY95KdwrrElOQn62+L
// SIG // 6UUDOTFbgh45wi9hDiZ5CQa2Y/OqzZrr4B+PUhwPCrZ7
// SIG // Bp9pKaLB/e9V+rureRlNIktBy9xB9E76kM2yQXDmH53f
// SIG // v8SKkkbVxnkd48Hka9lzRjX6krK4U3h4rVnCZscS8yPY
// SIG // 06Sfr7de/1S2pFIdSBZW8yY+Hv8pHo/sOIwX+0v1sRcK
// SIG // hW26Ma/CdX4liAPcQkQcaeGUpMj8zJi4RU3kaEIoXkRg
// SIG // KkxwahzWMUR8RAY6bj4DMexNdxop+wYhBECa7AukBfAZ
// SIG // f34W4AuJwU2uqSGj5PM/4o4c7BiTa+VJ+LYC6vusLMPE
// SIG // WDHRAF7jZMwoRgAeS6aBKI+qu6xJuHmsDJsmvEVDu/xQ
// SIG // 5ao/VUP1q8AoQcDsROATCJR62DQIwNbeRbQ6Dck1zEx0
// SIG // zWt8uyDG/KliITcifihb4uTop0lhM4eFighJAfFbOrii
// SIG // UNwLNdB1sv/h7aMCf6Fl5xoN4lZrXje7Hd2myeEKU4He
// SIG // f8nYuxx51cSmZSEm9rXoUwnPDU2LR/mnDikfUDcQgkbr
// SIG // VfzEjoxluyUAe6u4NIq5aNFtZ7dLO/MsP0u+MWyGkS9D
// SIG // e5Zp+7Pxhwyfdn+234eFMGZ6oan/js37XeO4Fbg=
// SIG // End signature block
