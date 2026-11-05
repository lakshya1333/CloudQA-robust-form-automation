/*
 * SmartLocator.js
 * ----------------
 * Finds form fields by what they MEAN to a human, not by fixed XPath/CSS.
 * Executed inside the page by Selenium (IJavaScriptExecutor). The C# side
 * passes a JSON "spec" describing each field (keywords, kind, desired value)
 * and receives back the chosen elements plus the evidence used.
 *
 * Signals used for every candidate control (each with its own weight):
 *   - native <label> association (for= / wrapping)         1.00
 *   - aria-label / aria-labelledby                          1.00
 *   - nearest preceding text in the DOM (slot-aware)        0.85
 *   - nearest label-looking text to the left / above        0.75
 *   - placeholder                                           0.70
 *   - title / name / data-test* attributes                  0.60
 *   - id attribute                                          0.50
 * Negative keywords (e.g. "last", "surname") subtract score so look-alike
 * fields are rejected. Radio groups are also recognised from their option
 * texts (Male/Female/...), dropdowns from containing the requested option.
 * Finally the "scope" (the <form>, or the document/shadow root) that holds the
 * best complete set of fields wins, so a similar decoy form elsewhere on the
 * page does not steal individual fields.
 *
 * The file is a single function expression: (function (spec) { ... })
 */
(function (spec) {
    'use strict';

    var MIN = typeof spec.minScore === 'number' ? spec.minScore : 0.45;
    var INCLUDE_SHADOW = spec.includeShadow !== false;

    // ------------------------------------------------------------------ text helpers
    function norm(s) {
        return String(s || '')
            .replace(/([a-z])([A-Z])/g, '$1 $2')
            .toLowerCase()
            .replace(/[^a-z0-9]+/g, ' ')
            .trim();
    }
    function compact(s) { return norm(s).replace(/ /g, ''); }

    // 1.0 = exact, 0.55..0.85 = keyword appears as whole word(s) in a longer text, 0 = no match
    function quality(text, keywords) {
        var n = norm(text);
        if (!n || !keywords || !keywords.length) return 0;
        var c = n.replace(/ /g, '');
        var best = 0;
        for (var i = 0; i < keywords.length; i++) {
            var kn = norm(keywords[i]);
            var kc = kn.replace(/ /g, '');
            if (!kc) continue;
            if (c === kc) { best = 1; break; }
            var re = new RegExp('(^| )' + kn.replace(/ /g, ' ?') + '( |$)');
            if (re.test(n)) {
                var extra = Math.max(0, n.split(' ').length - kn.split(' ').length);
                best = Math.max(best, Math.max(0.55, 0.85 - 0.1 * extra));
            }
        }
        return best;
    }

    var SKIP_TEXT_TAGS = { SCRIPT: 1, STYLE: 1, TEMPLATE: 1, SELECT: 1, OPTION: 1, TEXTAREA: 1, NOSCRIPT: 1 };
    function textOf(node) {
        if (!node) return '';
        if (node.nodeType === 3) return node.textContent;
        if (node.nodeType !== 1 || SKIP_TEXT_TAGS[node.tagName]) return '';
        var out = '';
        var kids = node.childNodes;
        for (var i = 0; i < kids.length; i++) out += ' ' + textOf(kids[i]);
        if (node.tagName === 'SLOT') {
            var assigned = node.assignedNodes ? node.assignedNodes({ flatten: true }) : [];
            for (var j = 0; j < assigned.length; j++) out += ' ' + textOf(assigned[j]);
        }
        return out.replace(/\s+/g, ' ').trim();
    }

    // ------------------------------------------------------------------ DOM helpers
    function isControl(n) {
        if (!n || n.nodeType !== 1) return false;
        var t = n.tagName;
        if (t === 'INPUT') return String(n.type).toLowerCase() !== 'hidden';
        return t === 'SELECT' || t === 'TEXTAREA' || t === 'BUTTON';
    }

    // composed-tree parent: follows slots and shadow-root hosts
    function parentOf(n) {
        if (n.assignedSlot) return n.assignedSlot;
        var p = n.parentNode;
        if (!p) return null;
        if (p.nodeType === 11) return p.host || null;
        return p;
    }

    function containsControl(n, exclude) {
        if (!n || n.nodeType !== 1) return false;
        if (isControl(n)) return !exclude.has(n);
        var list = n.querySelectorAll('input,select,textarea,button');
        for (var i = 0; i < list.length; i++) {
            if (!exclude.has(list[i]) && isControl(list[i])) return true;
        }
        var slots = n.tagName === 'SLOT' ? [n] : Array.prototype.slice.call(n.querySelectorAll('slot'));
        for (var s = 0; s < slots.length; s++) {
            var assigned = slots[s].assignedElements ? slots[s].assignedElements({ flatten: true }) : [];
            for (var a = 0; a < assigned.length; a++) if (containsControl(assigned[a], exclude)) return true;
        }
        if (n.shadowRoot) {
            var inner = n.shadowRoot.querySelectorAll('input,select,textarea,button');
            for (var k = 0; k < inner.length; k++) if (!exclude.has(inner[k]) && isControl(inner[k])) return true;
        }
        return false;
    }

    function visible(el) {
        if (!el || !el.isConnected) return false;
        var cs = window.getComputedStyle(el);
        if (cs.display === 'none' || cs.visibility === 'hidden' || cs.visibility === 'collapse') return false;
        if (!el.getClientRects().length) return false;
        var r = el.getBoundingClientRect();
        return r.width > 0 && r.height > 0;
    }

    function closestComposed(el, pred) {
        var cur = el;
        while (cur) {
            if (cur.nodeType === 1 && pred(cur)) return cur;
            cur = parentOf(cur);
        }
        return null;
    }

    function inShadow(el) { return el.getRootNode() !== document; }

    function describe(el) {
        if (!el) return '(none)';
        var s = '<' + el.tagName.toLowerCase();
        ['type', 'id', 'name', 'class', 'placeholder', 'value'].forEach(function (a) {
            var v = el.getAttribute(a);
            if (v !== null && v !== '' && !(a === 'value' && el.tagName !== 'INPUT')) {
                s += ' ' + a + '="' + (v.length > 40 ? v.slice(0, 40) + '…' : v) + '"';
            }
        });
        s += '>';
        if (inShadow(el)) s += ' [in shadow DOM]';
        return s;
    }

    // Walk the whole composed tree in document order (optionally entering open shadow roots)
    var ORDER = new Map();
    var ALL = [];
    (function walk(node) {
        if (node.nodeType === 1) {
            ORDER.set(node, ALL.length);
            ALL.push(node);
            if (INCLUDE_SHADOW && node.shadowRoot) {
                var sk = node.shadowRoot.children;
                for (var i = 0; i < sk.length; i++) walk(sk[i]);
            }
        }
        var kids = node.children || [];
        for (var j = 0; j < kids.length; j++) walk(kids[j]);
    })(document.documentElement);

    // Label-looking elements (short direct text) for the visual/geometric signal
    var LABEL_TAGS = { LABEL: 1, SPAN: 1, TD: 1, TH: 1, DIV: 1, P: 1, LEGEND: 1, B: 1, STRONG: 1, DT: 1, DD: 1,
        H1: 1, H2: 1, H3: 1, H4: 1, H5: 1, H6: 1, SMALL: 1, EM: 1, I: 1, LI: 1, FONT: 1 };
    var LABELS = null;
    function labelCandidates() {
        if (LABELS) return LABELS;
        LABELS = [];
        for (var i = 0; i < ALL.length; i++) {
            var e = ALL[i];
            if (!LABEL_TAGS[e.tagName]) continue;
            var direct = '';
            for (var k = 0; k < e.childNodes.length; k++) {
                if (e.childNodes[k].nodeType === 3) direct += e.childNodes[k].textContent;
            }
            direct = direct.replace(/\s+/g, ' ').trim();
            if (!direct || direct.length > 50) continue;
            if (!visible(e)) continue;
            LABELS.push({ el: e, text: direct, rect: e.getBoundingClientRect() });
        }
        return LABELS;
    }

    // ------------------------------------------------------------------ label signals
    // Nearest text that precedes the element inside its own "field container".
    // Climbs the composed tree; stops as soon as another control sits in between.
    function proximityLabel(el, exclude, skipTexts) {
        var cur = el;
        for (var depth = 0; depth < 6; depth++) {
            var p = parentOf(cur);
            if (!p || p.nodeType !== 1) return null;
            var sibParent = cur.assignedSlot ? null : cur.parentNode;
            if (sibParent) {
                var kids = Array.prototype.slice.call(sibParent.childNodes);
                var idx = kids.indexOf(cur);
                for (var i = idx - 1; i >= 0; i--) {
                    var s = kids[i];
                    var t;
                    if (s.nodeType === 3) {
                        t = s.textContent.replace(/\s+/g, ' ').trim();
                        if (t && t.length <= 60 && !(skipTexts && skipTexts.has(compact(t)))) return { text: t, node: cur.parentNode };
                        continue;
                    }
                    if (s.nodeType !== 1 || SKIP_TEXT_TAGS[s.tagName] || s.tagName === 'LINK') continue;
                    if (containsControl(s, exclude)) return null;
                    t = textOf(s);
                    if (t && t.length <= 60 && !(skipTexts && skipTexts.has(compact(t)))) return { text: t, node: s };
                }
                // floating-label style: a <label> right after the control
                if (depth === 0) {
                    for (var f = idx + 1; f < kids.length; f++) {
                        var fs = kids[f];
                        if (fs.nodeType !== 1) continue;
                        if (containsControl(fs, exclude)) break;
                        if (fs.tagName === 'LABEL') {
                            var ft = textOf(fs);
                            if (ft && ft.length <= 60) return { text: ft, node: fs };
                        }
                    }
                }
            }
            if (p.tagName === 'FORM' || p.tagName === 'BODY') return null;
            cur = p;
        }
        return null;
    }

    // Nearest label-looking text to the left (same row) or directly above.
    function geometricLabel(el, excludeNodes, skipTexts) {
        if (!visible(el)) return null;
        var r = el.getBoundingClientRect();
        var best = null, bestD = 1e9;
        var cands = labelCandidates();
        for (var i = 0; i < cands.length; i++) {
            var c = cands[i];
            if (c.el === el || c.el.contains(el) || el.contains(c.el)) continue;
            if (excludeNodes && excludeNodes.has(c.el)) continue;
            if (skipTexts && skipTexts.has(compact(c.text))) continue;
            if (containsControl(c.el, new Set())) continue;
            var b = c.rect, d = null;
            var vOverlap = Math.min(r.bottom, b.bottom) - Math.max(r.top, b.top);
            if (b.right <= r.left + 4 && vOverlap > Math.min(r.height, b.height) * 0.4) {
                d = r.left - b.right;
            } else if (b.bottom <= r.top + 4) {
                var hOverlap = Math.min(r.right, b.right) - Math.max(r.left, b.left);
                if (hOverlap > 0 || Math.abs(b.left - r.left) < 40) d = (r.top - b.bottom) * 1.2 + Math.abs(b.left - r.left) * 0.1;
            }
            if (d !== null && d < bestD && d < 220) { bestD = d; best = c; }
        }
        return best ? { text: best.text, node: best.el } : null;
    }

    function sourcesFor(el, opts) {
        opts = opts || {};
        var out = [];
        function add(type, w, text, node) {
            text = String(text || '').replace(/\s+/g, ' ').trim();
            if (text && text.length <= 120) out.push({ type: type, w: w, text: text, node: node || null });
        }
        if (!opts.noOwnLabels) {
            try {
                if (el.labels) for (var i = 0; i < el.labels.length; i++) add('label', 1.0, textOf(el.labels[i]), el.labels[i]);
            } catch (e) { /* ignore */ }
            add('aria-label', 1.0, el.getAttribute('aria-label'));
            var lb = el.getAttribute('aria-labelledby');
            if (lb) {
                var root = el.getRootNode();
                lb.split(/\s+/).forEach(function (id) {
                    var n = root.getElementById ? root.getElementById(id) : null;
                    if (n) add('aria-labelledby', 1.0, textOf(n), n);
                });
            }
        }
        var exclude = opts.exclude || new Set([el]);
        var start = opts.proximityStart || el;
        var prox = proximityLabel(start, exclude, opts.skipTexts);
        if (prox) add('nearby-text', 0.85, prox.text, prox.node);
        var geo = geometricLabel(start, opts.excludeNodes, opts.skipTexts);
        if (geo) add('visual-label', 0.75, geo.text, geo.node);
        if (!opts.noOwnAttributes) {
            add('placeholder', 0.7, el.getAttribute('placeholder'));
            add('title', 0.6, el.getAttribute('title'));
            add('name-attr', 0.6, el.getAttribute('name'));
            add('id-attr', 0.5, el.id);
            for (var a = 0; a < el.attributes.length; a++) {
                var at = el.attributes[a];
                if (/^data-(test|qa|cy|automation)/i.test(at.name)) add(at.name, 0.6, at.value);
            }
        }
        return out;
    }

    // Combine all signals into one score + human readable evidence
    // Sources a human can see. Attribute sources (id/name/title/data-*) only corroborate.
    var VISIBLE_TYPES = { 'label': 1, 'aria-label': 1, 'aria-labelledby': 1, 'nearby-text': 1, 'visual-label': 1, 'legend': 1, 'placeholder': 1 };

    function combine(best, sum, neg) { return best + 0.15 * Math.min(2, sum - best) - 0.9 * neg; }

    function scoreSources(srcs, f) {
        var best = 0, sum = 0, neg = 0, vbest = 0, vsum = 0, vneg = 0, evidence = [];
        for (var i = 0; i < srcs.length; i++) {
            var s = srcs[i];
            var vis = !!VISIBLE_TYPES[s.type];
            var q = quality(s.text, f.keywords);
            if (q === 0 && f.weakKeywords) q = quality(s.text, f.weakKeywords) * 0.35;
            var v = s.w * q;
            if (v > 0) evidence.push(s.type + ' "' + s.text + '" +' + v.toFixed(2));
            if (v > best) best = v;
            sum += v;
            if (vis) { if (v > vbest) vbest = v; vsum += v; }
            if (f.negativeKeywords && s.w >= 0.5 && quality(s.text, f.negativeKeywords) > 0) {
                neg = Math.max(neg, s.w);
                if (vis) vneg = Math.max(vneg, s.w);
                evidence.push(s.type + ' "' + s.text + '" contradicts -' + (0.9 * s.w).toFixed(2));
            }
        }
        return { score: combine(best, sum, neg), visual: combine(vbest, vsum, vneg), evidence: evidence };
    }

    // ------------------------------------------------------------------ option matching
    function optionMatch(text, value, desired) {
        var d = compact(desired);
        if (!d) return 0;
        if (compact(text) === d) return 1;
        if (value != null && compact(value) === d) return 0.95;
        var q = quality(text, [desired]);
        return q > 0 ? 0.6 * q : 0;
    }

    // ------------------------------------------------------------------ candidates per kind
    var TEXT_TYPES = { '': 1, text: 1, search: 1 };

    function textCandidates(f) {
        var res = [];
        for (var i = 0; i < ALL.length; i++) {
            var el = ALL[i];
            if (el.tagName !== 'INPUT') continue;
            var type = String(el.getAttribute('type') || '').toLowerCase();
            if (!TEXT_TYPES[type] && !(f.allowedInputTypes && f.allowedInputTypes.indexOf(type) >= 0)) continue;
            if (el.disabled || el.readOnly || !visible(el)) continue;
            var sc = scoreSources(sourcesFor(el), f);
            if (inShadow(el)) { sc.score -= 0.05; sc.visual -= 0.05; sc.evidence.push('inside shadow DOM -0.05'); }
            res.push({ el: el, score: sc.score, visual: sc.visual, evidence: sc.evidence, kind: 'text' });
        }
        return res;
    }

    // Explicit option label: wrapping <label>, <label for>, aria-label. Returns null if none.
    function radioExplicitLabel(r) {
        var wrap = closestComposed(r, function (n) { return n.tagName === 'LABEL'; });
        if (wrap) { var wt = textOf(wrap); if (wt) return { text: wt, node: wrap, clickable: wrap }; }
        try {
            if (r.labels && r.labels.length) {
                var lt = textOf(r.labels[0]);
                if (lt) return { text: lt, node: r.labels[0], clickable: r.labels[0] };
            }
        } catch (e) { /* ignore */ }
        var aria = r.getAttribute('aria-label');
        if (aria) return { text: aria, node: null, clickable: null };
        return null;
    }

    // Text written right after / right before the radio, never crossing another control.
    function radioAdjacentLabels(r) {
        function scan(dirNext) {
            var s = dirNext ? r.nextSibling : r.previousSibling;
            while (s) {
                if (s.nodeType === 3) {
                    var t = s.textContent.replace(/\s+/g, ' ').trim();
                    if (t) return { text: t, node: null, clickable: null };
                } else if (s.nodeType === 1) {
                    if (containsControl(s, new Set([r]))) return null;
                    if (s.tagName === 'LABEL' && s.control && s.control !== r) return null;
                    if (s.tagName === 'BR') { if (!dirNext) return null; }
                    var et = textOf(s);
                    if (et) return { text: et, node: s, clickable: s.tagName === 'LABEL' ? s : null };
                }
                s = dirNext ? s.nextSibling : s.previousSibling;
            }
            return null;
        }
        var after = scan(true), before = scan(false);
        // radio alone in a wrapper -> the wrapper's next sibling often holds the text
        if (!after) {
            var p = r.parentElement;
            if (p && p.querySelectorAll('input').length === 1 && p.nextElementSibling && !containsControl(p.nextElementSibling, new Set())) {
                var nt = textOf(p.nextElementSibling);
                if (nt && nt.length <= 40) after = { text: nt, node: p.nextElementSibling, clickable: p.nextElementSibling.tagName === 'LABEL' ? p.nextElementSibling : null };
            }
        }
        return { after: after, before: before };
    }

    // Decide per group whether option texts are written after (usual) or before the radios.
    function resolveOptionLabels(radios) {
        var adj = radios.map(radioAdjacentLabels);
        var allBefore = adj.every(function (a) { return a.before; });
        var lastHasAfter = !!adj[adj.length - 1].after;
        var useBefore = allBefore && !lastHasAfter;
        return radios.map(function (r, i) {
            var explicit = radioExplicitLabel(r);
            if (explicit) return explicit;
            var pick = useBefore ? adj[i].before : (adj[i].after || adj[i].before);
            if (pick) return pick;
            return { text: r.value || '', node: null, clickable: null, fromValue: true };
        });
    }

    function radioCandidates(f) {
        var groups = [];
        for (var i = 0; i < ALL.length; i++) {
            var r = ALL[i];
            if (r.tagName !== 'INPUT' || String(r.type).toLowerCase() !== 'radio' || r.disabled) continue;
            var root = r.getRootNode();
            var form = closestComposed(r, function (n) { return n.tagName === 'FORM'; });
            var name = r.getAttribute('name') || '';
            var key = name ? null : parentOf(r);
            var g = null;
            for (var k = 0; k < groups.length; k++) {
                var gg = groups[k];
                if (gg.root === root && gg.form === form && gg.name === name && (name || gg.key === key)) { g = gg; break; }
            }
            if (!g) { g = { root: root, form: form, name: name, key: key, radios: [] }; groups.push(g); }
            g.radios.push(r);
        }
        // resolve option texts per group, then keep only radios a user could actually operate
        groups.forEach(function (g) {
            var opts = resolveOptionLabels(g.radios);
            var keepR = [], keepO = [];
            g.radios.forEach(function (r, idx) {
                var o = opts[idx];
                if (visible(r) || (o.clickable && visible(o.clickable))) { keepR.push(r); keepO.push(o); }
            });
            g.radios = keepR;
            g.options = keepO;
        });
        groups = groups.filter(function (g) { return g.radios.length > 0; });
        var res = [];
        groups.forEach(function (g) {
            var first = g.radios[0];
            var exclude = new Set(g.radios);
            var excludeNodes = new Set();
            var skipTexts = new Set();
            g.options.forEach(function (o) { if (o.node) excludeNodes.add(o.node); if (o.text) skipTexts.add(compact(o.text)); });
            var srcs = [];
            var fs = closestComposed(first, function (n) { return n.tagName === 'FIELDSET'; });
            if (fs) {
                var lg = fs.querySelector('legend');
                if (lg && g.radios.every(function (x) { return fs.contains(x); })) srcs.push({ type: 'legend', w: 1.0, text: textOf(lg), node: lg });
            }
            var rg = closestComposed(first, function (n) { return n.getAttribute && n.getAttribute('role') === 'radiogroup'; });
            if (rg) {
                if (rg.getAttribute('aria-label')) srcs.push({ type: 'aria-label', w: 1.0, text: rg.getAttribute('aria-label') });
                var rlb = rg.getAttribute('aria-labelledby');
                if (rlb) {
                    var rn = rg.getRootNode().getElementById(rlb.split(/\s+/)[0]);
                    if (rn) srcs.push({ type: 'aria-labelledby', w: 1.0, text: textOf(rn), node: rn });
                }
            }
            srcs = srcs.concat(sourcesFor(first, {
                noOwnLabels: true, noOwnAttributes: true, exclude: exclude, excludeNodes: excludeNodes,
                skipTexts: skipTexts, proximityStart: first
            }));
            if (g.name) srcs.push({ type: 'name-attr', w: 0.6, text: g.name });
            srcs = srcs.filter(function (s) { return s.text; });
            var sc = scoreSources(srcs, f);

            // option vocabulary (e.g. Male / Female) identifies the group even without a label
            var vocabHits = 0;
            if (f.optionVocabulary) g.options.forEach(function (o) { if (quality(o.text, f.optionVocabulary) >= 0.85) vocabHits++; });
            if (vocabHits >= 2) { sc.score += 0.45; sc.visual += 0.45; sc.evidence.push(vocabHits + ' options look like ' + f.key + ' values +0.45'); }
            else if (vocabHits === 1) { sc.score += 0.2; sc.visual += 0.2; sc.evidence.push('1 option looks like a ' + f.key + ' value +0.20'); }

            // requested option must exist
            var bestIdx = -1, bestQ = 0;
            g.options.forEach(function (o, idx) {
                var q = optionMatch(o.text, g.radios[idx].value, f.desired);
                if (q > bestQ) { bestQ = q; bestIdx = idx; }
            });
            if (f.desired) {
                if (bestIdx < 0) { sc.score -= 0.6; sc.visual -= 0.6; sc.evidence.push('requested option "' + f.desired + '" not present -0.60'); }
                else sc.evidence.push('option "' + g.options[bestIdx].text + '" matches requested "' + f.desired + '"');
            }
            if (inShadow(first)) { sc.score -= 0.05; sc.visual -= 0.05; sc.evidence.push('inside shadow DOM -0.05'); }
            res.push({
                el: bestIdx >= 0 ? g.radios[bestIdx] : first,
                clickTarget: bestIdx >= 0 ? g.options[bestIdx].clickable : null,
                optionText: bestIdx >= 0 ? g.options[bestIdx].text : null,
                groupRadios: g.radios,
                allOptions: g.options.map(function (o) { return o.text; }),
                score: sc.score, visual: sc.visual, evidence: sc.evidence, kind: 'radio', orderEl: first
            });
        });
        return res;
    }

    function selectCandidates(f) {
        var res = [];
        for (var i = 0; i < ALL.length; i++) {
            var el = ALL[i];
            if (el.tagName !== 'SELECT' || el.disabled || el.multiple) continue;
            if (!visible(el) && !(spec.allowHiddenSelect)) continue;
            var sc = scoreSources(sourcesFor(el), f);
            var bestIdx = -1, bestQ = 0;
            for (var k = 0; k < el.options.length; k++) {
                var o = el.options[k];
                if (o.disabled) continue;
                var q = optionMatch(o.text, o.getAttribute('value'), f.desired);
                if (q > bestQ) { bestQ = q; bestIdx = k; }
            }
            if (f.desired) {
                if (bestIdx < 0) { sc.score -= 0.6; sc.visual -= 0.6; sc.evidence.push('requested option "' + f.desired + '" not present -0.60'); }
                else { sc.score += 0.2; sc.visual += 0.2; sc.evidence.push('contains option "' + el.options[bestIdx].text.trim() + '" +0.20'); }
            }
            if (f.optionVocabulary) {
                var hits = 0;
                for (var v = 0; v < el.options.length; v++) if (quality(el.options[v].text, f.optionVocabulary) >= 0.85) hits++;
                if (hits >= 3) { sc.score += 0.3; sc.visual += 0.3; sc.evidence.push(hits + ' options look like ' + f.key + ' values +0.30'); }
            }
            if (inShadow(el)) { sc.score -= 0.05; sc.visual -= 0.05; sc.evidence.push('inside shadow DOM -0.05'); }
            res.push({
                el: el, score: sc.score, visual: sc.visual, evidence: sc.evidence, kind: 'select',
                optionIndex: bestIdx, optionText: bestIdx >= 0 ? el.options[bestIdx].text.trim() : null,
                optionValue: bestIdx >= 0 ? el.options[bestIdx].value : null
            });
        }
        return res;
    }

    // ------------------------------------------------------------------ scope selection
    function scopeOf(el) {
        var form = closestComposed(el, function (n) { return n.tagName === 'FORM'; });
        return form || el.getRootNode();
    }
    function orderOf(c) { var o = ORDER.get(c.orderEl || c.el); return o === undefined ? 1e9 : o; }

    var perField = {};
    spec.fields.forEach(function (f) {
        var list = f.kind === 'radio' ? radioCandidates(f) : f.kind === 'select' ? selectCandidates(f) : textCandidates(f);
        list.forEach(function (c) { c.scope = scopeOf(c.orderEl || c.el); });
        list.sort(function (a, b) { return (b.score - a.score) || (orderOf(a) - orderOf(b)); });
        perField[f.key] = list;
    });

    // scopes that contain a qualifying candidate for every field
    var scopes = [];
    spec.fields.forEach(function (f) {
        perField[f.key].forEach(function (c) { if (c.score >= MIN && scopes.indexOf(c.scope) < 0) scopes.push(c.scope); });
    });
    // Among complete scopes, the highest total of HUMAN-VISIBLE evidence wins (ids/names are
    // exactly what changes between page versions, so they must not decide between two forms).
    // Scopes within 10% of the best are equally plausible: the one first on the page wins
    // (the primary form normally comes first; duplicates/widgets come later).
    var complete = [];
    scopes.forEach(function (s) {
        var total = 0, order = 1e9, ok = true;
        spec.fields.forEach(function (f) {
            var top = null;
            perField[f.key].forEach(function (c) { if (!top && c.scope === s && c.score >= MIN) top = c; });
            if (!top) ok = false; else { total += top.visual; order = Math.min(order, orderOf(top)); }
        });
        if (ok) complete.push({ scope: s, total: total, order: order });
    });
    var bestScope = null, bestTotal = -1e9;
    if (complete.length) {
        var maxTotal = Math.max.apply(null, complete.map(function (c) { return c.total; }));
        var near = complete.filter(function (c) { return c.total >= maxTotal * 0.9; });
        near.sort(function (a, b) { return a.order - b.order; });
        bestScope = near[0].scope;
        bestTotal = near[0].total;
    }

    function scopeLabel(s) {
        if (!s) return 'whole page (fields chosen individually)';
        if (s.nodeType === 1) return describe(s);
        return s === document ? 'document' : 'shadow root of <' + (s.host ? s.host.tagName.toLowerCase() : '?') + '>';
    }

    var result = { complete: !!bestScope, scope: scopeLabel(bestScope), fields: {}, url: location.href, title: document.title };
    spec.fields.forEach(function (f) {
        var list = perField[f.key];
        var pool = bestScope ? list.filter(function (c) { return c.scope === bestScope; }) : list;
        var chosen = pool.length && pool[0].score >= MIN ? pool[0] : null;
        var alts = list.filter(function (c) { return c !== chosen; }).slice(0, 3).map(function (c) {
            return describe(c.el) + ' score=' + c.score.toFixed(2);
        });
        var entry = {
            found: !!chosen,
            score: chosen ? Math.round(chosen.score * 100) / 100 : (list.length ? Math.round(list[0].score * 100) / 100 : 0),
            evidence: chosen ? chosen.evidence : (list.length ? list[0].evidence : []),
            description: chosen ? describe(chosen.el) : null,
            alternatives: alts,
            candidates: list.length
        };
        if (chosen) {
            entry.element = chosen.el;
            if (pool.length > 1 && pool[1].score >= MIN && chosen.score - pool[1].score < 0.1) {
                entry.warning = 'ambiguous: ' + describe(pool[1].el) + ' scored ' + pool[1].score.toFixed(2);
            }
            if (chosen.kind === 'radio') {
                entry.clickTarget = chosen.clickTarget || null;
                entry.optionText = chosen.optionText;
                entry.options = chosen.allOptions;
                entry.optionFound = chosen.optionText !== null;
            }
            if (chosen.kind === 'select') {
                entry.optionIndex = chosen.optionIndex;
                entry.optionText = chosen.optionText;
                entry.optionValue = chosen.optionValue;
                entry.optionFound = chosen.optionIndex >= 0;
            }
        }
        result.fields[f.key] = entry;
    });
    return result;
})
