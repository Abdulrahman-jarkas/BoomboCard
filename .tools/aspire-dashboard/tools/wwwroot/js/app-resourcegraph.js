import './d3.v7.min.js'

let resourceGraph = null;

export function initializeResourcesGraph(resourcesInterop) {
    resourceGraph = new ResourceGraph(resourcesInterop);
    resourceGraph.resize();

    const observer = new ResizeObserver(function () {
        resourceGraph.resize();
    });

    for (const child of document.getElementsByClassName('resources-summary-layout')) {
        observer.observe(child);
    }
}

export function updateResourcesGraph(resources) {
    if (resourceGraph) {
        resourceGraph.updateResources(resources);
    }
}

export function updateResourcesGraphSelected(resourceName) {
    if (resourceGraph) {
        resourceGraph.switchTo(resourceName);
    }
}

class ResourceGraph {
    constructor(resourcesInterop) {
        this.resources = [];
        this.resourcesInterop = resourcesInterop;
        this.openContextMenu = false;

        this.nodes = [];
        this.links = [];

        this.svg = d3.select('.resource-graph');
        this.baseGroup = this.svg.append("g");

        // Enable zoom + pan
        // https://www.d3indepth.com/zoom-and-pan/
        // scaleExtent limits zoom to reasonable values
        this.zoom = d3.zoom().scaleExtent([0.2, 4]).on('zoom', (event) => {
            this.baseGroup.attr('transform', event.transform);
        });
        this.svg.call(this.zoom);

        // simulation setup with all forces
        this.linkForce = d3
            .forceLink()
            .id(function (link) { return link.id })
            .strength(function (link) { return 1 })
            .distance(150);

        this.simulation = d3
            .forceSimulation()
            .force('link', this.linkForce)
            .force('charge', d3.forceManyBody().strength(-800))
            .force("collide", d3.forceCollide(110).iterations(10))
            .force("x", d3.forceX().strength(0.1))
            .force("y", d3.forceY().strength(0.2));

        // Drag start is trigger on mousedown from click.
        // Only change the state of the simulation when the drag event is triggered.
        var dragActive = false;
        var dragged = false;
        this.dragDrop = d3.drag().on('start', (event) => {
            dragActive = event.active;
            event.subject.fx = event.subject.x;
            event.subject.fy = event.subject.y;
        }).on('drag', (event) => {
            if (!dragActive) {
                this.simulation.alphaTarget(0.1).restart();
                dragActive = true;
            }
            dragged = true;
            event.subject.fx = event.x;
            event.subject.fy = event.y;
        }).on('end', (event) => {
            if (dragged) {
                this.simulation.alphaTarget(0);
                dragged = false;
            }
            event.subject.fx = null;
            event.subject.fy = null;
        });

        var defs = this.svg.append("defs");
        this.createArrowMarker(defs, "arrow-normal", "arrow-normal", 10, 10, 66);
        this.createArrowMarker(defs, "arrow-highlight", "arrow-highlight", 15, 15, 48);
        this.createArrowMarker(defs, "arrow-highlight-expand", "arrow-highlight-expand", 15, 15, 56);

        var highlightedPattern = defs.append("pattern")
            .attr("id", "highlighted-pattern")
            .attr("patternUnits", "userSpaceOnUse")
            .attr("width", "17.5")
            .attr("height", "17.5")
            .attr("patternTransform", "rotate(45)");

        highlightedPattern
            .append("rect")
            .attr("x", "0")
            .attr("y", "0")
            .attr("width", "17.5")
            .attr("height", "17.5")
            .attr("fill", "var(--fill-color)");

        highlightedPattern
            .append("line")
            .attr("x1", "0")
            .attr("y", "0")
            .attr("x2", "0")
            .attr("y2", "17.5")
            .attr("stroke", "var(--neutral-fill-secondary-hover)")
            .attr("stroke-width", "15");

        this.linkElementsG = this.baseGroup.append("g").attr("class", "links");
        this.nodeElementsG = this.baseGroup.append("g").attr("class", "nodes");

        this.initializeButtons();
    }

    initializeButtons() {
        d3.select('.graph-zoom-in').on("click", () => this.zoomIn());
        d3.select('.graph-zoom-out').on("click", () => this.zoomOut());
        d3.select('.graph-reset').on("click", () => this.resetZoomAndPan());
    }

    resetZoomAndPan() {
        this.svg.transition().call(this.zoom.transform, d3.zoomIdentity);
    }

    zoomIn() {
        this.svg.transition().call(this.zoom.scaleBy, 1.5);
    }

    zoomOut() {
        this.svg.transition().call(this.zoom.scaleBy, 2 / 3);
    }

    createArrowMarker(parent, id, className, width, height, x) {
        parent.append("marker")
            .attr("id", id)
            .attr("viewBox", "0 -5 10 10")
            .attr("refX", x)
            .attr("refY", 0)
            .attr("markerWidth", width)
            .attr("markerHeight", height)
            .attr("orient", "auto")
            .attr("markerUnits", "userSpaceOnUse")
            .attr("class", className)
            .append("path")
            .attr("d", 'M0,-5L10,0L0,5');
    }

    resize() {
        var container = document.querySelector(".resources-summary-layout");
        if (container) {
            var width = container.clientWidth;
            var height = Math.max(container.clientHeight - 50, 0);
            this.svg.attr("viewBox", [-width / 2, -height / 2, width, height]);
        }
    }

    switchTo(resourceName) {
        this.selectedNode = this.nodes.find(node => node.id === resourceName);
        this.updateNodeHighlights(null);
    }

    resourceEqual(r1, r2) {
        if (r1.name !== r2.name) {
            return false;
        }
        if (r1.displayName !== r2.displayName) {
            return false;
        }
        if (!this.iconEqual(r1.resourceIcon, r2.resourceIcon)) {
            return false;
        }
        if (r1.referencedNames.length !== r2.referencedNames.length) {
            return false;
        }
        for (var i = 0; i < r1.referencedNames.length; i++) {
            if (r1.referencedNames[i] !== r2.referencedNames[i]) {
                return false;
            }
        }

        return true;
    }

    iconEqual(i1, i2) {
        if (i1.path !== i2.path) {
            return false;
        }
        if (i1.color !== i2.color) {
            return false;
        }
        if (i1.tooltip !== i2.tooltip) {
            return false;
        }

        return true;
    }

    resourcesChanged(existingResource, newResources) {
        if (!existingResource || newResources.length != existingResource.length) {
            return true;
        }

        for (var i = 0; i < newResources.length; i++) {
            if (!this.resourceEqual(newResources[i], existingResource[i], false)) {
                return true;
            }
        }

        return false;
    }

    updateNodes(newResources) {
        const existingNodes = this.nodes || []; // Ensure nodes is initialized
        const updatedNodes = [];

        newResources.forEach(resource => {
            const existingNode = existingNodes.find(node => node.id === resource.name);

            if (existingNode) {
                // Update existing node without replacing it
                updatedNodes.push({
                    ...existingNode,
                    label: resource.displayName,
                    endpointUrl: resource.endpointUrl,
                    endpointText: resource.endpointText,
                    resourceIcon: createIcon(resource.resourceIcon),
                    stateIcon: createIcon(resource.stateIcon)
                });
            } else {
                // Add new resource
                updatedNodes.push({
                    id: resource.name,
                    label: resource.displayName,
                    endpointUrl: resource.endpointUrl,
                    endpointText: resource.endpointText,
                    resourceIcon: createIcon(resource.resourceIcon),
                    stateIcon: createIcon(resource.stateIcon)
                });
            }
        });

        this.nodes = updatedNodes;

        function createIcon(resourceIcon) {
            return {
                path: resourceIcon.path,
                color: resourceIcon.color,
                tooltip: resourceIcon.tooltip
            };
        }
    }

    updateResources(newResources) {
        // Check if the overall structure of the graph has changed. i.e. nodes or links have been added or removed.
        var hasStructureChanged = this.resourcesChanged(this.resources, newResources);

        this.resources = newResources;

        this.updateNodes(newResources);

        this.links = [];
        for (var i = 0; i < newResources.length; i++) {
            var resource = newResources[i];

            var resourceLinks = resource.referencedNames
                .filter((referencedName) => {
                    return newResources.some(r => r.name === referencedName);
                })
                .map((referencedName, index) => {
                    return {
                        id: `${resource.name}-${referencedName}`,
                        target: referencedName,
                        source: resource.name,
                        strength: 0.7
                    };
                });

            this.links.push(...resourceLinks);
        }

        // Update nodes
        this.nodeElements = this.nodeElementsG
            .selectAll(".resource-group")
            .data(this.nodes, n => n.id);

        // Remove excess nodes:
        this.nodeElements
            .exit()
            .transition()
            .attr("opacity", 0)
            .remove();

        // Resource node
        var newNodes = this.nodeElements
            .enter().append("g")
            .attr("class", "resource-group")
            .attr("opacity", 0)
            .attr("resource-name", n => n.id)
            .call(this.dragDrop);

        var newNodesContainer = newNodes
            .append("g")
            .attr("class", "resource-scale")
            .on('click', this.selectNode)
            .on('contextmenu', this.nodeContextMenu)
            .on('mouseover', this.hoverNode)
            .on('mouseout', this.unHoverNode);
        newNodesContainer
            .append("circle")
            .attr("r", 56)
            .attr("class", "resource-node")
            .attr("stroke", "white")
            .attr("stroke-width", "4");
        newNodesContainer
            .append("circle")
            .attr("r", 53)
            .attr("class", "resource-node-border");
        newNodesContainer
            .append("g")
            .attr("transform", "scale(2.1) translate(-12,-17)")
            .append("path")
            .attr("fill", n => n.resourceIcon.color)
            .attr("d", n => n.resourceIcon.path)
            .append("title")
            .text(n => n.resourceIcon.tooltip);

        var endpointGroup = newNodesContainer
            .append("g")
            .attr("transform", "translate(0,28)")
            .attr("class", "resource-endpoint");
        endpointGroup.append("text");
        endpointGroup.append("title");

        // Resource status
        var statusGroup = newNodesContainer
            .append("g")
            .attr("transform", "scale(1.6) translate(14,-34)");
        statusGroup
            .append("circle")
            .attr("r", 8)
            .attr("cy", 8)
            .attr("cx", 8)
            .attr("class", "resource-status-circle")
            .append("title");
        statusGroup
            .append("path")
            .attr("class", "resource-status-path")
            .append("title");

        var resourceNameGroup = newNodesContainer
            .append("g")
            .attr("transform", "translate(0,71)")
            .attr("class", "resource-name");
        resourceNameGroup
            .append("text")
            .text(n => trimText(n.label, 30));
        resourceNameGroup
            .append("title")
            .text(n => n.label);

        newNodes.transition()
            .attr("opacity", 1);

        this.nodeElements = newNodes.merge(this.nodeElements);

        // Set resource values that change.
        this.nodeElementsG
            .selectAll(".resource-group")
            .select(".resource-endpoint")
            .select("text")
            .text(n => trimText(n.endpointText, 15));
        this.nodeElementsG
            .selectAll(".resource-group")
            .select(".resource-endpoint")
            .select("title")
            .text(n => n.endpointText);
        this.nodeElementsG
            .selectAll(".resource-group")
            .select(".resource-status-circle")
            .select("title")
            .text(n => n.stateIcon.tooltip);
        this.nodeElementsG
            .selectAll(".resource-group")
            .select(".resource-status-path")
            .attr("d", n => n.stateIcon.path)
            .attr("fill", n => n.stateIcon.color)
            .select("title")
            .text(n => n.stateIcon.tooltip);

        // Update links
        this.linkElements = this.linkElementsG
            .selectAll("line")
            .data(this.links, (d) => { return d.id; });

        this.linkElements
            .exit()
            .transition()
            .attr("opacity", 0)
            .remove();

        var newLinks = this.linkElements
            .enter().append("line")
            .attr("opacity", 0)
            .attr("class", "resource-link");

        newLinks.transition()
            .attr("opacity", 1);

        this.linkElements = newLinks.merge(this.linkElements);

        this.simulation
            .nodes(this.nodes)
            .on('tick', this.onTick);

        this.simulation.force("link").links(this.links);
        if (hasStructureChanged) {
            this.simulation.alpha(1).restart();
        }
        else {
            this.simulation.restart();
        }

        function trimText(text, maxLength) {
            if (text.length > maxLength) {
                return text.slice(0, maxLength) + "\u2026";
            }
            return text;
        }
   }

    onTick = () => {
        this.nodeElements.attr("transform", function (d) { return "translate(" + d.x + "," + d.y + ")"; });
        this.linkElements
            .attr('x1', function (link) { return link.source.x })
            .attr('y1', function (link) { return link.source.y })
            .attr('x2', function (link) { return link.target.x })
            .attr('y2', function (link) { return link.target.y });
    }

    getNeighbors(node) {
        return this.links.reduce(function (neighbors, link) {
            if (link.target.id === node.id) {
                neighbors.push(link.source.id);
            } else if (link.source.id === node.id) {
                neighbors.push(link.target.id);
            }
            return neighbors;
        },
            [node.id]);
    }

    isNeighborLink(node, link) {
        return link.target.id === node.id || link.source.id === node.id
    }

    getLinkClass(nodes, selectedNode, link) {
        if (nodes.find(n => this.isNeighborLink(n, link))) {
            if (this.nodeEquals(selectedNode, link.target)) {
                return 'resource-link-highlight-expand';
            }
            return 'resource-link-highlight';
        }
        return 'resource-link';
    }

    nodeContextMenu = async (event) => {
        var data = event.target.__data__;

        // Prevent default browser context menu.
        event.preventDefault();

        this.openContextMenu = true;

        try {
            // Wait for method completion. It completes when the context menu is closed.
            await this.resourcesInterop.invokeMethodAsync('ResourceContextMenu', data.id, window.innerWidth, window.innerHeight, event.clientX, event.clientY);
        } finally {
            this.openContextMenu = false;

            // Unselect the node when the context menu is closed to reset mouseover state.
            this.updateNodeHighlights(null);
        }
    };

    selectNode = (event) => {
        var data = event.target.__data__;

        // Always send the clicked on resource to the server. It will clear the selection if the same resource is clicked again.
        this.resourcesInterop.invokeMethodAsync('SelectResource', data.id);

        // Unscale the previous selected node.
        if (this.selectedNode) {
            changeScale(this, this.selectedNode.id, 1);
        }

        // Scale selected node if it is not the same as the previous selected node.
        var clearSelection = this.nodeEquals(data, this.selectedNode);
        if (!clearSelection) {
            changeScale(this, data.id, 1.2);
        }

        this.selectedNode = data;

        function changeScale(self, id, scale) {
            let match = self.nodeElementsG
                .selectAll(".resource-group")
                .filter(function (d) {
                    return d.id == id;
                });

            match
                .select(".resource-scale")
                .transition()
                .duration(300)
                .style("transform", `scale(${scale})`)
                .on("end", s => {
                    match.select(".resource-scale").style("transform", null);
                    self.updateNodeHighlights(null);
                });
        }
    }

    hoverNode = (event) => {
        var mouseoverNode = event.target.__data__;

        this.updateNodeHighlights(mouseoverNode);
    }

    unHoverNode = (event) => {
        // Don't unhover the selected node when the context menu is open.
        // This is done to keep the node selected until the context menu is closed.
        if (!this.openContextMenu) {
            this.updateNodeHighlights(null);
        }
    };

    nodeEquals(resource1, resource2) {
        if (!resource1 || !resource2) {
            return false;
        }
        return resource1.id === resource2.id;
    }

    updateNodeHighlights = (mouseoverNode) => {
        var mouseoverNeighbors = mouseoverNode ? this.getNeighbors(mouseoverNode) : [];
        var selectNeighbors = this.selectedNode ? this.getNeighbors(this.selectedNode) : [];
        var neighbors = [...mouseoverNeighbors, ...selectNeighbors];

        // we modify the styles to highlight selected nodes
        this.nodeElements.attr('class', (node) => {
            var classNames = ['resource-group'];
            if (this.nodeEquals(node, mouseoverNode)) {
                classNames.push('resource-group-hover');
            }
            if (this.nodeEquals(node, this.selectedNode)) {
                classNames.push('resource-group-selected');
            }
            if (neighbors.indexOf(node.id) > -1) {
                classNames.push('resource-group-highlight');
            }
            return classNames.join(' ');
        });
        this.linkElements.attr('class', (link) => {
            var nodes = [];
            if (mouseoverNode) {
                nodes.push(mouseoverNode);
            }
            if (this.selectedNode) {
                nodes.push(this.selectedNode);
            }
            return this.getLinkClass(nodes, this.selectedNode, link);
        });
    };
};

// SIG // Begin signature block
// SIG // MIIpFwYJKoZIhvcNAQcCoIIpCDCCKQQCAQExDzANBglg
// SIG // hkgBZQMEAgEFADB3BgorBgEEAYI3AgEEoGkwZzAyBgor
// SIG // BgEEAYI3AgEeMCQCAQEEEBDgyQbOONQRoqMAEEvTUJAC
// SIG // AQACAQACAQACAQACAQAwMTANBglghkgBZQMEAgEFAAQg
// SIG // 1sRX6BZbREp6rvCCR0tJDYRQwZ9c0D0/oRA5UpDPdiOg
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
// SIG // AgEVMC8GCSqGSIb3DQEJBDEiBCBmtEm06mdHq03jdRJq
// SIG // jw0ay+IlJUuzf1/UsW6Zo1+xwzBCBgorBgEEAYI3AgEM
// SIG // MTQwMqAUgBIATQBpAGMAcgBvAHMAbwBmAHShGoAYaHR0
// SIG // cDovL3d3dy5taWNyb3NvZnQuY29tMA0GCSqGSIb3DQEB
// SIG // AQUABIIBgAR8tyw4gHYZ8CBENEH4ax43GUXDsPggk68Y
// SIG // bV9IrK9MfkYWd3NNSYtAVyYGUgycnR1KqSWnjokTHRWe
// SIG // ZU93ulPhB1xzwxqG5V+mRE5pRdp962Csn/B4c9xSNjfH
// SIG // nWQ3I0AJosyhiL8+CYzKEydGQnOr/L8DG5leaWY9D5/S
// SIG // uUl/qmPulhVZKsUAcHvqW+RKSC02FP2dU/nvg8KMRUtF
// SIG // Ma+sqKcqMmdzOn7Wv8WIHMiIVeJid+AZSsYleMfrLZGL
// SIG // KoFf8r+XWjRF8aJ4VsDzguhRlssjvPxk7+z4GgyiJUS8
// SIG // fJQHXbTG/zmLk4gFZpBJlfbTW5XIDVIzU6hMN+RvFitV
// SIG // MEMUZr01r7N4nuAqsIu7O5O2UYo7bvVTr7jDv3a/RqDB
// SIG // FPy8pEFgC1cYKlM1OO4od2vBdJkQoRR2wXyNGhzyGByD
// SIG // zEUVzXvbgza6DZh8xzut0qaciFQ6qR6BAqnUBR5Qn12W
// SIG // kwoVKWKws/N7+7OjqTyBb4YSrNlReFIdUosw76GCF5Qw
// SIG // gheQBgorBgEEAYI3AwMBMYIXgDCCF3wGCSqGSIb3DQEH
// SIG // AqCCF20wghdpAgEDMQ8wDQYJYIZIAWUDBAIBBQAwggFS
// SIG // BgsqhkiG9w0BCRABBKCCAUEEggE9MIIBOQIBAQYKKwYB
// SIG // BAGEWQoDATAxMA0GCWCGSAFlAwQCAQUABCCp7Uh3urhv
// SIG // tO7wUeSq8z3B8zOUo3mpHbbFk9Smv6wF3QIGaMLHYo6P
// SIG // GBMyMDI1MTAwMzE3NDkzOC4zOTVaMASAAgH0oIHRpIHO
// SIG // MIHLMQswCQYDVQQGEwJVUzETMBEGA1UECBMKV2FzaGlu
// SIG // Z3RvbjEQMA4GA1UEBxMHUmVkbW9uZDEeMBwGA1UEChMV
// SIG // TWljcm9zb2Z0IENvcnBvcmF0aW9uMSUwIwYDVQQLExxN
// SIG // aWNyb3NvZnQgQW1lcmljYSBPcGVyYXRpb25zMScwJQYD
// SIG // VQQLEx5uU2hpZWxkIFRTUyBFU046QTkzNS0wM0UwLUQ5
// SIG // NDcxJTAjBgNVBAMTHE1pY3Jvc29mdCBUaW1lLVN0YW1w
// SIG // IFNlcnZpY2WgghHqMIIHIDCCBQigAwIBAgITMwAAAgy5
// SIG // ZOM1nOz0rgABAAACDDANBgkqhkiG9w0BAQsFADB8MQsw
// SIG // CQYDVQQGEwJVUzETMBEGA1UECBMKV2FzaGluZ3RvbjEQ
// SIG // MA4GA1UEBxMHUmVkbW9uZDEeMBwGA1UEChMVTWljcm9z
// SIG // b2Z0IENvcnBvcmF0aW9uMSYwJAYDVQQDEx1NaWNyb3Nv
// SIG // ZnQgVGltZS1TdGFtcCBQQ0EgMjAxMDAeFw0yNTAxMzAx
// SIG // OTQzMDBaFw0yNjA0MjIxOTQzMDBaMIHLMQswCQYDVQQG
// SIG // EwJVUzETMBEGA1UECBMKV2FzaGluZ3RvbjEQMA4GA1UE
// SIG // BxMHUmVkbW9uZDEeMBwGA1UEChMVTWljcm9zb2Z0IENv
// SIG // cnBvcmF0aW9uMSUwIwYDVQQLExxNaWNyb3NvZnQgQW1l
// SIG // cmljYSBPcGVyYXRpb25zMScwJQYDVQQLEx5uU2hpZWxk
// SIG // IFRTUyBFU046QTkzNS0wM0UwLUQ5NDcxJTAjBgNVBAMT
// SIG // HE1pY3Jvc29mdCBUaW1lLVN0YW1wIFNlcnZpY2UwggIi
// SIG // MA0GCSqGSIb3DQEBAQUAA4ICDwAwggIKAoICAQDKAVYm
// SIG // PeRtga/U6jzqyqLD0MAool23gcBN58+Z/XskYwNJsZ+O
// SIG // +wVyQYl8dPTK1/BC2xAic1m+JvckqjVaQ32KmURsEZot
// SIG // irQY4PKVW+eXwRt3r6szgLuic6qoHlbXox/l0HJtgURk
// SIG // zDXWMkKmGSL7z8/crqcvmYqv8t/slAF4J+mpzb9tMFVm
// SIG // jwKXONVdRwg9Q3WaPZBC7Wvoi7PRIN2jgjSBnHYyAZSl
// SIG // stKNrpYb6+Gu6oSFkQzGpR65+QNDdkP4ufOf4PbOg3fb
// SIG // 4uGPjI8EPKlpwMwai1kQyX+fgcgCoV9J+o8MYYCZUet3
// SIG // kzhhwRzqh6LMeDjaXLP701SXXiXc2ZHzuDHbS/sZtJ36
// SIG // 27cVpClXEIUvg2xpr0rPlItHwtjo1PwMCpXYqnYKvX8a
// SIG // J8nawT9W8FUuuyZPG1852+q4jkVleKL7x+7el8ETehbd
// SIG // kwdhAXyXimaEzWetNNSmG/KfHAp9czwsL1vKr4Rgn+pI
// SIG // IkZHuomdf5e481K+xIWhLCPdpuV87EqGOK/jbhOnZEqw
// SIG // dvA0AlMaLfsmCemZmupejaYuEk05/6cCUxgF4zCnkJeY
// SIG // dMAP+9Z4kVh7tzRFsw/lZSl2D7EhIA6Knj6RffH2k7Yt
// SIG // SGSv86CShzfiXaz9y6sTu8SGqF6ObL/eu/DkivyVoCfU
// SIG // XWLjiSJsrS63D0EHHQIDAQABo4IBSTCCAUUwHQYDVR0O
// SIG // BBYEFHUORSH/sB/rQ/beD0l5VxQ706GIMB8GA1UdIwQY
// SIG // MBaAFJ+nFV0AXmJdg/Tl0mWnG1M1GelyMF8GA1UdHwRY
// SIG // MFYwVKBSoFCGTmh0dHA6Ly93d3cubWljcm9zb2Z0LmNv
// SIG // bS9wa2lvcHMvY3JsL01pY3Jvc29mdCUyMFRpbWUtU3Rh
// SIG // bXAlMjBQQ0ElMjAyMDEwKDEpLmNybDBsBggrBgEFBQcB
// SIG // AQRgMF4wXAYIKwYBBQUHMAKGUGh0dHA6Ly93d3cubWlj
// SIG // cm9zb2Z0LmNvbS9wa2lvcHMvY2VydHMvTWljcm9zb2Z0
// SIG // JTIwVGltZS1TdGFtcCUyMFBDQSUyMDIwMTAoMSkuY3J0
// SIG // MAwGA1UdEwEB/wQCMAAwFgYDVR0lAQH/BAwwCgYIKwYB
// SIG // BQUHAwgwDgYDVR0PAQH/BAQDAgeAMA0GCSqGSIb3DQEB
// SIG // CwUAA4ICAQDZMPr4gVmwwf4GMB5ZfHSr34uhug6yzu4H
// SIG // UT+JWMZqz9uhLZBoX5CPjdKJzwAVvYoNuLmS0+9lA5S7
// SIG // 4rvKqd/u9vp88VGk6U7gMceatdqpKlbVRdn2ZfrMcpI4
// SIG // zOc6BtuYrzJV4cEs1YmX95uiAxaED34w02BnfuPZXA0e
// SIG // dsDBbd4ixFU8X/1J0DfIUk1YFYPOrmwmI2k16u6TcKO0
// SIG // YpRlwTdCq9vO0eEIER1SLmQNBzX9h2ccCvtgekOaBoIQ
// SIG // 3ZRai8Ds1f+wcKCPzD4qDX3xNgvLFiKoA6ZSG9S/yOrG
// SIG // aiSGIeDy5N9VQuqTNjryuAzjvf5W8AQp31hV1GbUDOkb
// SIG // Udd+zkJWKX4FmzeeN52EEbykoWcJ5V9M4DPGN5xpFqXy
// SIG // 9aO0+dR0UUYWuqeLhDyRnVeZcTEu0xgmo+pQHauFVASs
// SIG // VORMp8TF8dpesd+tqkkQ8VNvI20oOfnTfL+7ZgUMf7qN
// SIG // V0ll0Wo5nlr1CJva1bfk2Hc5BY1M9sd3blBkezyvJPn4
// SIG // j0bfOOrCYTwYsNsjiRl/WW18NOpiwqciwFlUNqtWCRMz
// SIG // C9r84YaUMQ82Bywk48d4uBon5ZA8pXXS7jwJTjJj5USe
// SIG // Rl9vjT98PDZyCFO2eFSOFdDdf6WBo/WZUA2hGZ0q+J7j
// SIG // 140fbXCfOUIm0j23HaAV0ckDS/nmC/oF1jCCB3EwggVZ
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
// SIG // czEnMCUGA1UECxMeblNoaWVsZCBUU1MgRVNOOkE5MzUt
// SIG // MDNFMC1EOTQ3MSUwIwYDVQQDExxNaWNyb3NvZnQgVGlt
// SIG // ZS1TdGFtcCBTZXJ2aWNloiMKAQEwBwYFKw4DAhoDFQDv
// SIG // u8hkhEMt5Z8Ldefls7z1LVU8pqCBgzCBgKR+MHwxCzAJ
// SIG // BgNVBAYTAlVTMRMwEQYDVQQIEwpXYXNoaW5ndG9uMRAw
// SIG // DgYDVQQHEwdSZWRtb25kMR4wHAYDVQQKExVNaWNyb3Nv
// SIG // ZnQgQ29ycG9yYXRpb24xJjAkBgNVBAMTHU1pY3Jvc29m
// SIG // dCBUaW1lLVN0YW1wIFBDQSAyMDEwMA0GCSqGSIb3DQEB
// SIG // CwUAAgUA7IpDwTAiGA8yMDI1MTAwMzEyNDQ0OVoYDzIw
// SIG // MjUxMDA0MTI0NDQ5WjB0MDoGCisGAQQBhFkKBAExLDAq
// SIG // MAoCBQDsikPBAgEAMAcCAQACAgbHMAcCAQACAhMMMAoC
// SIG // BQDsi5VBAgEAMDYGCisGAQQBhFkKBAIxKDAmMAwGCisG
// SIG // AQQBhFkKAwKgCjAIAgEAAgMHoSChCjAIAgEAAgMBhqAw
// SIG // DQYJKoZIhvcNAQELBQADggEBABqJFw4taXFxoY3bUmae
// SIG // qb5qGQcN9WB4z6awubFHlzwxiuGVsrMLbDFD7ri0gipS
// SIG // pvqDYLY7J9H0U2ptVHFfkpW1gN1YAIGbp1Fpr+iV9QVA
// SIG // 2T/Mu79ZBbOypxDwj0mTQ1xvn8ze6yYsttxAAvMvIhMG
// SIG // yHqIZvEMCa7IOfgL9xHn1h426GoCSCLvH+UMV7zWLr5p
// SIG // EQs/7tPWQ4B5POTXP+ctTr8Le9fHMC9pqUWDwN3kbWQe
// SIG // iWgmFEghRz1P77lQCRbXHscoIAixG/xBGkuEha3Oi54Y
// SIG // eGXkn1yyK4pSng1CPpMLuXv7OA6OjJRpCHk06j1HkHdX
// SIG // 71lVnzmJDbj/J+cxggQNMIIECQIBATCBkzB8MQswCQYD
// SIG // VQQGEwJVUzETMBEGA1UECBMKV2FzaGluZ3RvbjEQMA4G
// SIG // A1UEBxMHUmVkbW9uZDEeMBwGA1UEChMVTWljcm9zb2Z0
// SIG // IENvcnBvcmF0aW9uMSYwJAYDVQQDEx1NaWNyb3NvZnQg
// SIG // VGltZS1TdGFtcCBQQ0EgMjAxMAITMwAAAgy5ZOM1nOz0
// SIG // rgABAAACDDANBglghkgBZQMEAgEFAKCCAUowGgYJKoZI
// SIG // hvcNAQkDMQ0GCyqGSIb3DQEJEAEEMC8GCSqGSIb3DQEJ
// SIG // BDEiBCDI1hp67AFb2qwgIEq/knELmP0dS8IxUeiI4jHJ
// SIG // 2vL0gDCB+gYLKoZIhvcNAQkQAi8xgeowgecwgeQwgb0E
// SIG // INUo17cFMZN46MI5NfIAg9Ux5cO5xM9inre5riuOZ8It
// SIG // MIGYMIGApH4wfDELMAkGA1UEBhMCVVMxEzARBgNVBAgT
// SIG // Cldhc2hpbmd0b24xEDAOBgNVBAcTB1JlZG1vbmQxHjAc
// SIG // BgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjEmMCQG
// SIG // A1UEAxMdTWljcm9zb2Z0IFRpbWUtU3RhbXAgUENBIDIw
// SIG // MTACEzMAAAIMuWTjNZzs9K4AAQAAAgwwIgQg/erHJCrz
// SIG // 97e3fF0TStjR4nxEf+cnF5Xcvtxew9sQnBEwDQYJKoZI
// SIG // hvcNAQELBQAEggIASy50cFbCbQJWQ4Kxw51S8io+rUmh
// SIG // Elq+alxhUCQAoeuRpTDvkAJINsPx/EBUrXDohZOdVipB
// SIG // AxA9eOuBzVsG5ndyauFerlDAVVaWoajZPSLCImHvqRFc
// SIG // IOspFo/Vbl2bkbr480S1nJHvvH9RAXAXfaL+o7phOpEo
// SIG // W8SwTTG4Q7yl/gRXsT4z80fBJTMpyhqAqo1ZfifasVCf
// SIG // +ZzVM6l+4Z9X/j/pBENaM1wDZ6Nz3BwSxL1wKiVokl2Y
// SIG // C6R9l8OqRaQV8e1CEp27X0xbh+fbOHBZ61G4ZngdIyTt
// SIG // 2yEjELmFd8IIiyuxIv6tbkSEajbkVaqG+xYw/lhZzkW2
// SIG // Pm7YeXgP18UyxefLfZDc39zh/BT+dnK8cUiPl6pd0crm
// SIG // qLl3fjnozjjqe2srGZR8i+O02Om4U9sa932SasQ4pOkM
// SIG // n2JmZv1wKG0Idj7U946Y2n26OHEuZLc5z/XFYmjKN0JE
// SIG // hX/HAg0SoWPmRYvYeFzb7LyqaPRruLOlVMePt2UoSO0o
// SIG // 8uO0LDRJaqLHC/IoDgniZNZsVXXswqyHg4dWsUu0/AOE
// SIG // N/R4agk03W4S34i5dS/s9CEEcWFC0+kUjIkbwzWi3RC0
// SIG // h3ZudP8xRmG7q2HiS9PtZXMV21rSM9Q4nxJC0x3yKQXo
// SIG // iep+inRTfD+P0qkYNPb2ddzXfgdckwe04VYRxIU=
// SIG // End signature block
