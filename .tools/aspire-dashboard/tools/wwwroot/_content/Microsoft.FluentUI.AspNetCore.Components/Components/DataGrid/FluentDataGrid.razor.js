let grids = [];
const minWidth = 100;

export function init(gridElement, autoFocus) {
    if (gridElement === undefined || gridElement === null) {
        return;
    };

    enableColumnResizing(gridElement);

    let start = gridElement.querySelector('td:first-child');

    if (autoFocus) {
        start.focus();
    }

    const bodyClickHandler = event => {
        const columnOptionsElement = gridElement?.querySelector('.col-options');
        if (columnOptionsElement && event.composedPath().indexOf(columnOptionsElement) < 0) {
            gridElement.dispatchEvent(new CustomEvent('closecolumnoptions', { bubbles: true }));
        }
        const columnResizeElement = gridElement?.querySelector('.col-resize');
        if (columnResizeElement && event.composedPath().indexOf(columnResizeElement) < 0) {
            gridElement.dispatchEvent(new CustomEvent('closecolumnresize', { bubbles: true }));
        }
    };
    const keyboardNavigation = (sibling) => {
        if (sibling !== null) {
            start.focus();
            sibling.focus();
            start = sibling;
        }
    }
    const keyDownHandler = event => {
        if (document.activeElement.tagName.toLowerCase() != 'table' && document.activeElement.tagName.toLowerCase() != 'td' && document.activeElement.tagName.toLowerCase() != 'th') {
            return;
        }
        const columnOptionsElement = gridElement?.querySelector('.col-options');
        if (columnOptionsElement) {
            if (event.key === "Escape") {
                gridElement.dispatchEvent(new CustomEvent('closecolumnoptions', { bubbles: true }));
                gridElement.focus();
            }
            columnOptionsElement.addEventListener(
                "keydown",
                (event) => {
                    if (event.key === "ArrowRight" || event.key === "ArrowLeft" || event.key === "ArrowDown" || event.key === "ArrowUp") {
                        event.stopPropagation();
                    }
                }
            );
        }
        const columnResizeElement = gridElement?.querySelector('.col-resize');
        if (columnResizeElement) {
            if (event.key === "Escape") {
                gridElement.dispatchEvent(new CustomEvent('closecolumnresize', { bubbles: true }));
                gridElement.focus();
            }
            columnResizeElement.addEventListener(
                "keydown",
                (event) => {
                    if (event.key === "ArrowRight" || event.key === "ArrowLeft" || event.key === "ArrowDown" || event.key === "ArrowUp") {
                        event.stopPropagation();
                    }
                }
            );
        }

        // check if start is a child of gridElement
        if (start !== null && (gridElement.contains(start) || gridElement === start) && document.activeElement === start && document.activeElement.tagName.toLowerCase() !== 'fluent-text-field' && document.activeElement.tagName.toLowerCase() !== 'fluent-menu-item') {
            const idx = start.cellIndex;

            if (event.key === "ArrowUp") {
                // up arrow
                const previousRow = start.parentElement.previousElementSibling;
                if (previousRow !== null) {
                    event.preventDefault();
                    const previousSibling = previousRow.cells[idx];
                    keyboardNavigation(previousSibling);
                }
            } else if (event.key === "ArrowDown") {
                // down arrow
                const nextRow = start.parentElement.nextElementSibling;
                if (nextRow !== null) {
                    event.preventDefault();
                    const nextSibling = nextRow.cells[idx];
                    keyboardNavigation(nextSibling);
                }
            } else if (event.key === "ArrowLeft") {
                // left arrow
                event.preventDefault();
                const previousSibling = (document.body.dir === '' || document.body.dir === 'ltr') ? start.previousElementSibling : start.nextElementSibling;
                keyboardNavigation(previousSibling);
                event.stopPropagation();
            } else if (event.key === "ArrowRight") {
                // right arrow
                event.preventDefault();
                const nextsibling = (document.body.dir === '' || document.body.dir === 'ltr') ? start.nextElementSibling : start.previousElementSibling;
                keyboardNavigation(nextsibling);
                event.stopPropagation();
            }
        }
        else {
            start = document.activeElement;
        }

    };

    const cells = gridElement.querySelectorAll('[role="gridcell"]');
    cells.forEach((cell) => {
        cell.columnDefinition = {
            columnDataKey: "",
            cellInternalFocusQueue: true,
            cellFocusTargetCallback: (cell) => {
                return cell.children[0];
            }
        }
        cell.addEventListener(
            "keydown",
            (event) => {
                if (event.target.role !== "gridcell" && (event.key === "ArrowRight" || event.key === "ArrowLeft")) {
                    event.stopPropagation();
                }
            }
        );
    });

    document.body.addEventListener('click', bodyClickHandler);
    document.body.addEventListener('mousedown', bodyClickHandler); // Otherwise it seems strange that it doesn't go away until you release the mouse button
    gridElement.addEventListener('keydown', keyDownHandler);

    return {
        stop: () => {
            document.body.removeEventListener('click', bodyClickHandler);
            document.body.removeEventListener('mousedown', bodyClickHandler);
            gridElement.removeEventListener('keydown', keyDownHandler);
            delete grids[gridElement];
        }
    };
}

export function checkColumnPopupPosition(gridElement, selector) {
    const colPopup = gridElement.querySelector(selector);
    if (colPopup) {
        const gridRect = gridElement.getBoundingClientRect();
        const popupRect = colPopup.getBoundingClientRect();
        const leftOverhang = Math.max(0, gridRect.left - popupRect.left);
        const rightOverhang = Math.max(0, popupRect.right - gridRect.right);
        if (leftOverhang || rightOverhang) {
            const applyOffset = leftOverhang && rightOverhang ? (leftOverhang - rightOverhang) / 2 : (leftOverhang - rightOverhang);
            colPopup.style.transform = `translateX(${applyOffset}px)`;
        }

        colPopup.style.visibility = 'visible';
        colPopup.scrollIntoViewIfNeeded();

        const autoFocusElem = colPopup.querySelector('[autofocus]');
        if (autoFocusElem) {
            autoFocusElem.focus();
        }
    }
}

export function enableColumnResizing(gridElement, resizeColumnOnAllRows = true) {
    const columns = [];
    const headers = gridElement.querySelectorAll('.column-header.resizable');

    if (headers.length === 0) {
        return;
    }

    const isRTL = getComputedStyle(gridElement).direction === 'rtl';
    const isGrid = gridElement.classList.contains('grid')

    let tableHeight = gridElement.offsetHeight;
    // rows have not been loaded yet, so we need to calculate the height
    if (tableHeight < 70) {
        // by getting the aria rowcount attribute
        const rowCount = gridElement.getAttribute('aria-rowcount');
        if (rowCount) {
            const rowHeight = gridElement.querySelector('thead tr th').offsetHeight;
            // and multiply by the itemsize (== height of the header cells)
            tableHeight = rowCount * rowHeight;
        }
    }

    // Determine the height based on the resizeColumnOnAllRows parameter
    let resizeHandleHeight = tableHeight;
    if (!resizeColumnOnAllRows) {
        // Only use the header height when resizeColumnOnAllRows is false
        // Use the first header's height if available
        resizeHandleHeight = headers.length > 0 ? (headers[0].offsetHeight - 14 ): 30; // fallback to 30px if no headers
    }

    headers.forEach((header) => {
        columns.push({
            header,
            size: `${header.clientWidth}px`,
        });

        // remove any previously created divs
        const resizedivs  = header.querySelectorAll(".actual-resize-handle");
        resizedivs.forEach(div => div.remove());

        // add a new resize div
        const div = createDiv(resizeHandleHeight, isRTL);
        header.appendChild(div);
        setListeners(div, isRTL);
    });

    let initialWidths;
    if (gridElement.style.gridTemplateColumns) {
        initialWidths = gridElement.style.gridTemplateColumns;
    } else {
        initialWidths = columns.map(({ size }) => size).join(' ');

        if (isGrid) {
            gridElement.style.gridTemplateColumns = initialWidths;
        }
    }

    const id = gridElement.id;
    grids.push({
        id,
        columns,
        initialWidths,
    });

    function setListeners(div, isRTL) {
        let pageX, curCol, curColWidth;

        div.addEventListener('pointerdown', function (e) {
            curCol = e.target.parentElement;
            pageX = e.pageX;

            const padding = paddingDiff(curCol);

            curColWidth = curCol.offsetWidth - padding;
        });

        div.addEventListener('pointerover', function (e) {
            e.target.style.borderInlineEnd = 'var(--fluent-data-grid-resize-handle-width) solid var(--fluent-data-grid-resize-handle-color)';
            e.target.previousElementSibling.style.visibility = 'hidden';
        });

        div.addEventListener('pointerup', removeBorder);
        div.addEventListener('pointercancel', removeBorder);
        div.addEventListener('pointerleave', removeBorder);

        document.addEventListener('pointermove', (e) =>
            requestAnimationFrame(() => {
                gridElement.style.tableLayout = 'fixed';

                if (curCol) {
                    const diffX = isRTL ? pageX - e.pageX : e.pageX - pageX;
                    const column = columns.find(({ header }) => header === curCol);

                    column.size = parseInt(Math.max(minWidth, curColWidth + diffX), 10) + 'px';

                    columns.forEach((col) => {
                        if (col.size.startsWith('minmax')) {
                            col.size = parseInt(col.header.clientWidth, 10) + 'px';
                        }
                    });

                    if (isGrid) {
                        gridElement.style.gridTemplateColumns = columns
                            .map(({ size }) => size)
                            .join(' ');
                    }
                    else {
                        curCol.style.width = column.size;
                    }
                }
            })
        );

        document.addEventListener('pointerup', function () {
            curCol = undefined;
            curColWidth = undefined;
            pageX = undefined;
        });
    }

    function createDiv(height, isRTL) {
        const div = document.createElement('div');
        div.className = "actual-resize-handle";
        div.style.top = '5px';
        div.style.position = 'absolute';
        div.style.cursor = 'col-resize';
        div.style.userSelect = 'none';
        div.style.height = (height - 5) + 'px';
        div.style.width = '6px';
        div.style.opacity = 'var(--fluent-data-grid-header-opacity)'

        if (isRTL) {
            div.style.left = '0px';
            div.style.right = 'unset';
        } else {
            div.style.left = 'unset';
            div.style.right = '0px';
        }
        return div;
    }

    function paddingDiff(col) {
        if (getStyleVal(col, 'box-sizing') === 'border-box') {
            return 0;
        }

        const padLeft = getStyleVal(col, 'padding-left');
        const padRight = getStyleVal(col, 'padding-right');
        return parseInt(padLeft) + parseInt(padRight);
    }

    function getStyleVal(elm, css) {
        return window.getComputedStyle(elm, null).getPropertyValue(css);
    }

    function removeBorder(e) {
        e.target.style.borderInlineEnd = '';
        e.target.previousElementSibling.style.visibility = 'visible';
    }
}



export function resetColumnWidths(gridElement) {
    const isGrid = gridElement.classList.contains('grid');
    const grid = grids.find(({ id }) => id === gridElement.id);
    if (!grid) {
        return;
    }

    const columnsWidths = grid.initialWidths.split(' ');

    grid.columns.forEach((column, index) => {
        if (isGrid) {
            column.size = columnsWidths[index];
        } else {
            column.header.style.width = columnsWidths[index];
        }
    });

    if (isGrid) {
        gridElement.style.gridTemplateColumns = grid.initialWidths;
    }
    gridElement.dispatchEvent(
        new CustomEvent('closecolumnresize', { bubbles: true })
    );
    gridElement.focus();
}

export function resizeColumnDiscrete(gridElement, column, change) {

    const columns = [];
    let headerBeingResized;

    if (!column) {
        const targetElement = document.activeElement.parentElement.parentElement.parentElement.parentElement;
        if (!(targetElement.classList.contains("column-header") && targetElement.classList.contains("resizable"))) {
            return;
        }
        headerBeingResized = targetElement;
    }
    else {
        headerBeingResized = gridElement.querySelector('.column-header[col-index="' + column + '"]');
    }


    grids.find(({ id }) => id === gridElement.id).columns.forEach(column => {
        if (column.header === headerBeingResized) {
            const width = headerBeingResized.getBoundingClientRect().width + change;

            if (change < 0) {
                column.size = Math.max(minWidth, width) + 'px';
            }
            else {
                column.size = width + 'px';
            }
        }
        else {
            if (column.size.startsWith('minmax')) {
                column.size = parseInt(column.header.clientWidth, 10) + 'px';
            }
        }
        columns.push(column.size);
    });

    gridElement.style.gridTemplateColumns = columns.join(' ');
}

export function resizeColumnExact(gridElement, column, width) {
    const columns = [];
    let headerBeingResized = gridElement.querySelector('.column-header[col-index="' + column + '"]');

    if (!headerBeingResized) {
        return;
    }

    grids.find(({ id }) => id === gridElement.id).columns.forEach(column => {
        if (column.header === headerBeingResized) {
            column.size = Math.max(minWidth, width) + 'px';
        }
        else {
            if (column.size.startsWith('minmax')) {
                column.size = parseInt(column.header.clientWidth, 10) + 'px';
            }
        }
        columns.push(column.size);
    });

    gridElement.style.gridTemplateColumns = columns.join(' ');

    gridElement.dispatchEvent(new CustomEvent('closecolumnresize', { bubbles: true }));
    gridElement.focus();
}

export function autoFitGridColumns(gridElement, columnCount) {
    let gridTemplateColumns = '';

    for (var i = 0; i < columnCount; i++) {
        const columnWidths = Array
            .from(gridElement.querySelectorAll(`[col-index="${i + 1}"]`))
            .flatMap((x) => x.offsetWidth);

        const maxColumnWidth = Math.max(...columnWidths);

        gridTemplateColumns += ` ${maxColumnWidth}px`;
    }

    gridElement.style.gridTemplateColumns = gridTemplateColumns;
    gridElement.classList.remove("auto-fit");

    grids[gridElement.id] = gridTemplateColumns;
}

function calculateVisibleRows(gridElement, rowHeight) {
    if (rowHeight <= 0) {
        return 0;
    }

    const gridContainer = gridElement.parentElement;

    if (!gridContainer) {
        return 0;
    }

    const availableHeight = gridContainer?.clientHeight || window.visualViewport?.height || window.innerHeight;

    const visibleRows = Math.max(Math.floor(availableHeight / rowHeight), 1);
    return visibleRows;
}

export function dynamicItemsPerPage(gridElement, dotNetObject, rowSize) {
    const observer = new ResizeObserver(() => {
        const visibleRows = calculateVisibleRows(gridElement, rowSize)
        dotNetObject.invokeMethodAsync('UpdateItemsPerPageAsync', visibleRows)
            .catch(err => console.error("Error invoking Blazor method:", err));
    });

    const targetElement = gridElement.parentElement;
    if (targetElement) {
        observer.observe(targetElement);
    }
}

// SIG // Begin signature block
// SIG // MIIpFwYJKoZIhvcNAQcCoIIpCDCCKQQCAQExDzANBglg
// SIG // hkgBZQMEAgEFADB3BgorBgEEAYI3AgEEoGkwZzAyBgor
// SIG // BgEEAYI3AgEeMCQCAQEEEBDgyQbOONQRoqMAEEvTUJAC
// SIG // AQACAQACAQACAQACAQAwMTANBglghkgBZQMEAgEFAAQg
// SIG // egDjxmZMwmZZYAWzvZ943g0HW6ZXhw+dAJPPj56pTeGg
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
// SIG // AgEVMC8GCSqGSIb3DQEJBDEiBCCrQ52eeHJPR58IBqh3
// SIG // I8ObPLGRBLPdLR488IM30XEfajBCBgorBgEEAYI3AgEM
// SIG // MTQwMqAUgBIATQBpAGMAcgBvAHMAbwBmAHShGoAYaHR0
// SIG // cDovL3d3dy5taWNyb3NvZnQuY29tMA0GCSqGSIb3DQEB
// SIG // AQUABIIBgL1qlnZ+clYR5A6ZnPfoDpdY4/VzrDj3V8wa
// SIG // lFGnz3Zyn74amYbyARoqbyw4BYkn162wJ1OFTZicM+9r
// SIG // dFLFtV/Tw+XnQnzBiuIhoM9vFF2dzEzjwEzb/Myri4G7
// SIG // iJDOJ8KeAd/WvhYo/Njrcu/ap3c7IAfTkZDJNvwIvduZ
// SIG // nmjADj9P4iF0ERhFH+/pCt5ZfQk3efzW2xVBlm7SDSsi
// SIG // V6pQxsa30Csm6HR0yKoEH3Xk9xAzjmJRWls+kQ3BbNar
// SIG // DCgVokaVn/KvOcRusz/5HbYDiqzi1ROf00vHHX52vis+
// SIG // x0p9tmO+R6DHMx96EGeq9m1hXJztMqSe5u4w7p5MlGF0
// SIG // oPibrkmFxNHPUNpbin6IOTH2isAn31d2chRxbs/0bbVj
// SIG // P5foAhWS/h8MLpt1bGe4cs6szTnX6/iAWn1IQ1h/atIp
// SIG // QOFVzdojy11xtI5PpzE5YV+oU5vlgX1WU8Mb20oBm0In
// SIG // bUPVmn8+8IcxrSHIPh520m2JUo7ZvU8bgHfqjqGCF5Qw
// SIG // gheQBgorBgEEAYI3AwMBMYIXgDCCF3wGCSqGSIb3DQEH
// SIG // AqCCF20wghdpAgEDMQ8wDQYJYIZIAWUDBAIBBQAwggFS
// SIG // BgsqhkiG9w0BCRABBKCCAUEEggE9MIIBOQIBAQYKKwYB
// SIG // BAGEWQoDATAxMA0GCWCGSAFlAwQCAQUABCBgnbjo/MY7
// SIG // M+/NcAOQ8AYalegoqQFkAezLsf79oZ1GmwIGaNsNPWeP
// SIG // GBMyMDI1MTAwMzE3NTAwNi4xMTZaMASAAgH0oIHRpIHO
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
// SIG // BDEiBCAS4iPNtR8WZEJwhWwVCz7O7nRh5JZhr0ZUIuZA
// SIG // FgwrETCB+gYLKoZIhvcNAQkQAi8xgeowgecwgeQwgb0E
// SIG // IIANAz3ceY0umhdWLR2sJpq0OPqtJDTAYRmjHVkwEW9I
// SIG // MIGYMIGApH4wfDELMAkGA1UEBhMCVVMxEzARBgNVBAgT
// SIG // Cldhc2hpbmd0b24xEDAOBgNVBAcTB1JlZG1vbmQxHjAc
// SIG // BgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjEmMCQG
// SIG // A1UEAxMdTWljcm9zb2Z0IFRpbWUtU3RhbXAgUENBIDIw
// SIG // MTACEzMAAAIFPHVsgkSHzf4AAQAAAgUwIgQghHZmyT4L
// SIG // tzkqyAeAmC6KbZZ3XDM6OtS3hvGKS0loX58wDQYJKoZI
// SIG // hvcNAQELBQAEggIAUyH+Tg/QMuzQJKc9m0Sv8+9ZE2rA
// SIG // ofrUdTQ7hHecQDOutO9Vv8AxAw4QUdBERXV9QYgrk4yb
// SIG // 1K6RfoS04hX5Dw4L1ivVZLK+YJ9PeavjLlu+6Fz+8JHU
// SIG // EuXFTF7qOnB+rcl7Z9EHN/lXmbJHppTh1twc92EVTWEn
// SIG // H2sQa2AS/zaf3msWgKxGqur9nUk3rgBcTBf9zHgoOpUo
// SIG // vbaOUNXkRT9Q+GgHpVGMnxa4qawuAA9YpwNgO95osrhA
// SIG // Ih83O+OMXmqUZKUp0EugCVEoN7kSv6QhflAf5JqH2I30
// SIG // l04gLe9UPgymVH3ysM6TQio9LYkUbiA/Rnpn3WirTXAD
// SIG // u51aXHqNjEZfGZL2uupeeZeLUMmmlbVlcPDQaFEuc/xa
// SIG // lM/6EF+7dkjUTXp1yZBAcUkKzmuMSI53pLrmCGZ1nKgu
// SIG // dNTCujWDL/pnztDyPESZddgbeWtccVp2C8grt/T5ZCBc
// SIG // ZfDxXJWB7snhYoW8NzNP/wh15x+thYMrAwDyHXkLS8x3
// SIG // oAzhpYg4L5hBs5pD0tuLlzmIeC43tjOTHK3FXk564tEd
// SIG // PnSgUfCb9HXSuqUB8dqGy50NWMXjZhUm98zGkqy8/CQM
// SIG // D4zfnOwmllCoCWN8lpEwWYWe/M8NlADuw0aVLDE2gYaU
// SIG // pbt1PjSFPhOvjYlLYmhuz8MT7Wd7o+kPyE++e5s=
// SIG // End signature block
