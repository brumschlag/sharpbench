# Dataset

SharpBench vendors [microsoft/SWE-Sharp-Bench](https://huggingface.co/datasets/microsoft/SWE-Sharp-Bench)
at `data/swe-sharp-bench.csv` (150 cases, SWE-bench schema).

The CSV contains upstream repository patches and test metadata from real open-source
projects. Those patches are third-party code excerpts, not original SharpBench work.

## Citation

```bibtex
@misc{mhatre2025swesharpbenchreproduciblebenchmarkc,
      title={SWE-Sharp-Bench: A Reproducible Benchmark for C# Software Engineering Tasks},
      author={Sanket Mhatre and Yasharth Bajpai and Sumit Gulwani and Emerson Murphy-Hill and Gustavo Soares},
      year={2025},
      eprint={2511.02352},
      archivePrefix={arXiv},
      primaryClass={cs.SE},
      url={https://arxiv.org/abs/2511.02352},
}
```

## Refresh from Hugging Face

To replace the vendored copy with the latest upstream release:

```bash
python3 -c "from huggingface_hub import hf_hub_download; import shutil; \
shutil.copy(hf_hub_download('microsoft/SWE-Sharp-Bench','swe-sharp-bench.csv',repo_type='dataset'),'data/swe-sharp-bench.csv')"
```
