// fire native graphics: the image slicer - turns a mask into tool paths of a fixed line width (Euclidean distance transform, marching squares, simplification).
// A port of src/fire.Terminal/Slicing/ImageSlicer.cs; the same paths come out (the order of the contours too: a contour starts at the first edge that was linked).
#pragma once

#include <cmath>
#include <cstdint>
#include <unordered_map>
#include <unordered_set>
#include <vector>
#include "fire_gfx.hpp"

namespace fire {
namespace gfx {

struct PointD { double x, y; };
enum PathKind { PATH_FILL = 0, PATH_OUTLINE = 1 };
enum FillStrategy { STRATEGY_CONTOUR = 0, STRATEGY_ZIGZAG = 1, STRATEGY_OUTLINE_ONLY = 2 };
struct ToolPath { std::vector<PointD> points; bool closed; int kind; };

struct Slicer {
    double lineWidth, pixelSize, overlap = 0.5, simplifyTolerance;   // simplifyTolerance: NaN = a quarter of a pixel
    int strategy = STRATEGY_CONTOUR;
    bool flipY = true;

    double stepOver() const { return lineWidth * (1.0 - overlap); }

    std::vector<ToolPath> slice(const Framebuffer& fb) const {
        int w = fb.width, h = fb.height;
        std::vector<char> mask((size_t)w * h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) {
                size_t i = (size_t)y * w + x;
                mask[(size_t)x * h + y] = fb.indices ? fb.indices[i] != 0 : ((fb.pixels[i] & 0x00FFFFFFu) != 0 && (fb.pixels[i] >> 24) != 0);   // mask[x][y]
            }
        return sliceMask(mask, w, h);
    }

private:
    /// A grid indexed [x][y], x major.
    struct Grid {
        int W = 0, H = 0;
        std::vector<double> v;
        double& at(int x, int y) { return v[(size_t)x * H + y]; }
        double at(int x, int y) const { return v[(size_t)x * H + y]; }
    };

    static void edt1d(const std::vector<double>& f, int n, std::vector<double>& d, std::vector<int>& v, std::vector<double>& z) {
        int k = 0;
        v[0] = 0;
        z[0] = -INFINITY;
        z[1] = INFINITY;
        for (int q = 1; q < n; q++) {
            double s = ((f[(size_t)q] + (double)q * q) - (f[(size_t)v[(size_t)k]] + (double)v[(size_t)k] * v[(size_t)k])) / (2.0 * q - 2.0 * v[(size_t)k]);
            while (s <= z[(size_t)k]) {
                k--;
                s = ((f[(size_t)q] + (double)q * q) - (f[(size_t)v[(size_t)k]] + (double)v[(size_t)k] * v[(size_t)k])) / (2.0 * q - 2.0 * v[(size_t)k]);
            }
            k++;
            v[(size_t)k] = q;
            z[(size_t)k] = s;
            z[(size_t)k + 1] = INFINITY;
        }
        k = 0;
        for (int q = 0; q < n; q++) {
            while (z[(size_t)k + 1] < q) k++;
            double dq = q - v[(size_t)k];
            d[(size_t)q] = dq * dq + f[(size_t)v[(size_t)k]];
        }
    }

    static Grid distanceField(const std::vector<char>& mask, int w, int h) {
        Grid d;
        d.W = w + 2; d.H = h + 2;
        d.v.assign((size_t)d.W * d.H, 0.0);
        double inf = (double)(d.W + d.H) * (d.W + d.H);
        for (int y = 0; y < d.H; y++)
            for (int x = 0; x < d.W; x++) {
                bool inside = x >= 1 && x <= w && y >= 1 && y <= h && mask[(size_t)(x - 1) * h + (y - 1)];
                d.at(x, y) = inside ? inf : 0.0;
            }
        int n = std::max(d.W, d.H);
        std::vector<double> f((size_t)n), outp((size_t)n), z((size_t)n + 1);
        std::vector<int> v((size_t)n);
        for (int x = 0; x < d.W; x++) {
            for (int y = 0; y < d.H; y++) f[(size_t)y] = d.at(x, y);
            edt1d(f, d.H, outp, v, z);
            for (int y = 0; y < d.H; y++) d.at(x, y) = outp[(size_t)y];
        }
        for (int y = 0; y < d.H; y++) {
            for (int x = 0; x < d.W; x++) f[(size_t)x] = d.at(x, y);
            edt1d(f, d.W, outp, v, z);
            for (int x = 0; x < d.W; x++) d.at(x, y) = std::sqrt(outp[(size_t)x]);
        }
        return d;
    }

    using Contour = std::vector<std::pair<double, double>>;

    static std::vector<Contour> traceContours(const Grid& d, double level) {
        int W = d.W, H = d.H;
        std::unordered_map<int64_t, std::vector<int64_t>> adj;
        std::vector<int64_t> order;   // the keys in the order they were added (the iteration order of the VM's dictionary)
        auto hkey = [&](int x, int y) { return ((int64_t)y * W + x) << 1; };
        auto vkey = [&](int x, int y) { return (((int64_t)y * W + x) << 1) | 1; };
        auto half = [&](int64_t from, int64_t to) {
            auto it = adj.find(from);
            if (it == adj.end()) { it = adj.emplace(from, std::vector<int64_t>()).first; order.push_back(from); }
            it->second.push_back(to);
        };
        auto link = [&](int64_t e1, int64_t e2) { half(e1, e2); half(e2, e1); };
        for (int y = 0; y < H - 1; y++)
            for (int x = 0; x < W - 1; x++) {
                double a = d.at(x, y), b = d.at(x + 1, y), c = d.at(x + 1, y + 1), e = d.at(x, y + 1);
                int idx = (a >= level ? 8 : 0) | (b >= level ? 4 : 0) | (c >= level ? 2 : 0) | (e >= level ? 1 : 0);
                if (idx == 0 || idx == 15) continue;
                int64_t T = hkey(x, y), R = vkey(x + 1, y), B = hkey(x, y + 1), L = vkey(x, y);
                bool centerIn = (a + b + c + e) / 4.0 >= level;
                switch (idx) {
                    case 1: case 14: link(L, B); break;
                    case 2: case 13: link(B, R); break;
                    case 3: case 12: link(L, R); break;
                    case 4: case 11: link(T, R); break;
                    case 6: case 9: link(T, B); break;
                    case 7: case 8: link(L, T); break;
                    case 5: if (centerIn) { link(L, T); link(B, R); } else { link(T, R); link(L, B); } break;
                    case 10: if (centerIn) { link(L, B); link(T, R); } else { link(L, T); link(B, R); } break;
                }
            }
        auto edgePoint = [&](int64_t key) -> std::pair<double, double> {
            int64_t cell = key >> 1;
            int x = (int)(cell % W), y = (int)(cell / W);
            if ((key & 1) == 0) { double v0 = d.at(x, y), v1 = d.at(x + 1, y); return {x + (level - v0) / (v1 - v0), (double)y}; }
            double v0 = d.at(x, y), v1 = d.at(x, y + 1);
            return {(double)x, y + (level - v0) / (v1 - v0)};
        };
        std::unordered_set<int64_t> visited;
        std::vector<Contour> contours;
        for (int64_t start : order) {
            if (visited.count(start)) continue;
            Contour poly;
            int64_t prev = -1, cur = start;
            while (true) {
                visited.insert(cur);
                poly.push_back(edgePoint(cur));
                const std::vector<int64_t>& neighbours = adj[cur];
                int64_t next = neighbours[0] != prev ? neighbours[0] : neighbours.size() > 1 ? neighbours[1] : -1;
                if (next == -1 || next == start || visited.count(next)) break;
                prev = cur;
                cur = next;
            }
            if (poly.size() >= 2) contours.push_back(std::move(poly));
        }
        return contours;
    }

    PointD toMm(double gx, double gy, int h) const {
        double x = (gx - 0.5) * pixelSize;
        double y = flipY ? (h - (gy - 0.5)) * pixelSize : (gy - 0.5) * pixelSize;
        return {x, y};
    }

    static double segmentDistance(PointD p, PointD a, PointD b) {
        double dx = b.x - a.x, dy = b.y - a.y, len2 = dx * dx + dy * dy;
        double t = len2 > 0 ? ((p.x - a.x) * dx + (p.y - a.y) * dy) / len2 : 0;
        t = std::max(0.0, std::min(1.0, t));
        double px = a.x + t * dx - p.x, py = a.y + t * dy - p.y;
        return std::sqrt(px * px + py * py);
    }

    /// Douglas-Peucker (a closed contour is simplified with its first point repeated).
    static std::vector<PointD> simplify(const std::vector<PointD>& pts, double tol) {
        if (tol <= 0 || pts.size() < 4) return pts;
        std::vector<PointD> work(pts);
        work.push_back(pts[0]);
        std::vector<char> keep(work.size(), 0);
        keep[0] = keep.back() = 1;
        std::vector<std::pair<int, int>> stack;
        stack.push_back({0, (int)work.size() - 1});
        while (!stack.empty()) {
            auto [s, e] = stack.back();
            stack.pop_back();
            double maxDist = 0;
            int index = -1;
            for (int i = s + 1; i < e; i++) {
                double dist = segmentDistance(work[(size_t)i], work[(size_t)s], work[(size_t)e]);
                if (dist > maxDist) { maxDist = dist; index = i; }
            }
            if (index >= 0 && maxDist > tol) { keep[(size_t)index] = 1; stack.push_back({s, index}); stack.push_back({index, e}); }
        }
        std::vector<PointD> result;
        for (size_t i = 0; i < work.size(); i++) if (keep[i]) result.push_back(work[i]);
        result.pop_back();
        return result.size() >= 2 ? result : pts;
    }

    std::vector<ToolPath> toPaths(const std::vector<Contour>& contours, int h, int kind) const {
        double tol = std::isnan(simplifyTolerance) ? pixelSize * 0.25 : simplifyTolerance;
        std::vector<ToolPath> result;
        for (const Contour& contour : contours) {
            std::vector<PointD> pts;
            for (auto [x, y] : contour) pts.push_back(toMm(x, y, h));
            result.push_back({simplify(pts, tol), true, kind});
        }
        return result;
    }

    std::vector<ToolPath> hatch(const Grid& d, double level, double stepPx, int h) const {
        std::vector<ToolPath> paths;
        bool leftToRight = true;
        int lastRow = -1;
        for (double gy = 1.0; gy <= h; gy += stepPx) {
            int row = (int)std::nearbyint(gy);   // Math.Round: half to even
            if (row == lastRow) continue;
            lastRow = row;
            std::vector<std::pair<double, double>> segments;
            double start = 0;
            for (int x = 1; x < d.W; x++) {
                double v0 = d.at(x - 1, row), v1 = d.at(x, row);
                bool prevIn = v0 >= level, curIn = v1 >= level;
                if (!prevIn && curIn) start = (x - 1) + (level - v0) / (v1 - v0);
                else if (prevIn && !curIn) segments.push_back({start, (x - 1) + (level - v0) / (v1 - v0)});
            }
            if (segments.empty()) continue;
            if (!leftToRight) {
                std::reverse(segments.begin(), segments.end());
                for (auto& s : segments) std::swap(s.first, s.second);
            }
            for (auto [x0, x1] : segments) paths.push_back({{toMm(x0, row, h), toMm(x1, row, h)}, false, PATH_FILL});
            leftToRight = !leftToRight;
        }
        return paths;
    }

    std::vector<ToolPath> sliceMask(const std::vector<char>& mask, int w, int h) const {
        Grid dist = distanceField(mask, w, h);
        double radiusPx = lineWidth / 2.0 / pixelSize;
        double stepPx = stepOver() / pixelSize;
        double baseLevel = radiusPx + 0.5;   // distances are measured from pixel center to pixel center: the real edge is half a pixel further out
        double maxDist = 0;
        for (double v : dist.v) if (v > maxDist) maxDist = v;
        std::vector<ToolPath> result;
        if (baseLevel > maxDist) return result;   // no place is wide enough for the line width
        std::vector<ToolPath> outline = toPaths(traceContours(dist, baseLevel), h, PATH_OUTLINE);
        if (strategy == STRATEGY_CONTOUR) {
            std::vector<ToolPath> rings;
            for (double level = baseLevel + stepPx; level < maxDist; level += stepPx) {
                std::vector<ToolPath> ring = toPaths(traceContours(dist, level), h, PATH_FILL);
                for (auto& p : ring) rings.push_back(std::move(p));
            }
            std::reverse(rings.begin(), rings.end());   // start inside, work outwards
            for (auto& p : rings) result.push_back(std::move(p));
        } else if (strategy == STRATEGY_ZIGZAG) {
            std::vector<ToolPath> hatched = hatch(dist, baseLevel, stepPx, h);
            for (auto& p : hatched) result.push_back(std::move(p));
        }
        for (auto& p : outline) result.push_back(std::move(p));   // the edge contour last, as the finishing path
        return result;
    }
};

}  // namespace gfx
}  // namespace fire
