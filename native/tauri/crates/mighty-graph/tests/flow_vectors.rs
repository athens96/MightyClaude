//! native/contracts/fixtures/flow-vectors.json (C# FlowVectorVerification, MightyCode.Core.Tests/FlowVectorVerification.cs): the role
//! view's layout of each case's runs, as the web page lays it out.

mod view_support;

use std::collections::HashSet;

use mighty_core::Wire;
use mighty_core::graph::MightyGraphRun;
use mighty_graph::layout::{self, FanOut, Options};
use serde_json::Value;
use view_support::*;

#[track_caller]
fn near(actual: f64, expected: f64, why: &str) {
    assert!((actual - expected).abs() <= 0.001, "{why}: expected {expected} but was {actual}");
}

/// "flow vectors: the role view lays out native/contracts/fixtures/flow-vectors.json as the web page does"
/// (FlowVectorVerification.TheSharedCasesLayOutAsThePageDoes).
#[test]
fn the_shared_cases_lay_out_as_the_page_does() {
    let fixture = read_json(&contracts().join("fixtures/flow-vectors.json"));
    let cases = fixture["cases"].as_array().expect("cases");
    check(cases.len() >= 7, format!("the fixture has its cases; got {}", cases.len()));
    for case in cases {
        let name = case["name"].as_str().unwrap_or("");
        let runs: Vec<MightyGraphRun> =
            case["runs"].as_array().expect("runs").iter().map(|r| MightyGraphRun::read(r).unwrap_or_else(|e| panic!("{name}: {e:?}"))).collect();
        let opened: HashSet<String> = case["opened"]
            .as_array()
            .expect("opened")
            .iter()
            .filter_map(Value::as_str)
            .flat_map(|suffix| runs.iter().map(move |run| layout::node_id(run, suffix)))
            .collect();
        let options = Options { splits_execution: true, fan_out: Some(FanOut::opened(opened)), ..Options::default() };
        let made = layout::make(&runs, "", case["running"].as_bool().unwrap_or(false), &HashSet::new(), &options, &ko());
        let expect = &case["expect"];
        let nodes = expect["nodes"].as_array().expect("nodes");
        check(
            made.nodes.len() == nodes.len(),
            format!(
                "{name}: {} nodes expected, {} laid out: {}",
                nodes.len(),
                made.nodes.len(),
                made.nodes.iter().map(|n| n.id.as_str()).collect::<Vec<_>>().join(", ")
            ),
        );
        for (index, (node, want)) in made.nodes.iter().zip(nodes).enumerate() {
            let label = format!("{name}: node {index}");
            check(node.id == want["id"].as_str().unwrap_or(""), format!("{label}: id {} expected, {} laid out", want["id"], node.id));
            check(node.kind == want["kind"].as_str().unwrap_or(""), format!("{label}: kind {} expected, {} laid out", want["kind"], node.kind));
            check(node.is_compact == want.get("compact").and_then(Value::as_bool).unwrap_or(false), format!("{label}: card"));
            near(node.frame.x, want["x"].as_f64().unwrap(), &format!("{label} x"));
            near(node.frame.y, want["y"].as_f64().unwrap(), &format!("{label} y"));
            near(node.frame.w, want["w"].as_f64().unwrap(), &format!("{label} w"));
            near(node.frame.h, want["h"].as_f64().unwrap(), &format!("{label} h"));
        }
        let edges: Vec<(String, String, bool, bool)> = expect["edges"]
            .as_array()
            .expect("edges")
            .iter()
            .map(|e| {
                (
                    e["source"].as_str().unwrap_or("").to_string(),
                    e["target"].as_str().unwrap_or("").to_string(),
                    e["joins"].as_bool().unwrap_or(false),
                    e.get("side").and_then(Value::as_bool).unwrap_or(false),
                )
            })
            .collect();
        let laid: Vec<(String, String, bool, bool)> = made.edges.iter().map(|e| (e.source.clone(), e.target.clone(), e.joins, e.side)).collect();
        check(laid == edges, format!("{name}: edges expected {edges:?}, laid out {laid:?}"));
        near(made.size.w, expect["size"]["w"].as_f64().unwrap(), &format!("{name}: width"));
        near(made.size.h, expect["size"]["h"].as_f64().unwrap(), &format!("{name}: height"));
        near(made.origin_x, expect["originX"].as_f64().unwrap(), &format!("{name}: origin x"));
        near(made.origin_y, expect["originY"].as_f64().unwrap(), &format!("{name}: origin y"));
    }
}
