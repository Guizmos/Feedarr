"""Only comparison invariants; no synthetic matching engine or provider framework."""
import copy, unittest
from bench import compare

class ComparisonTests(unittest.TestCase):
    def run_data(self, decision='reject', proposed=None):
        return {'completed':True,'datasetHash':'frozen','engine':{},'entries':[{'id':1,'benchmarkStatus':None,'entry':{
            'providerEvidenceComplete':True,'providerEvidenceFingerprint':'same','queryPlanFingerprint':'same','searchLocale':'fr-FR',
            'detail':{'decision':decision,'selectedIdentity':proposed}}}]}
    def test_without_review_does_not_invent_correctness(self):
        x=self.run_data('accept',{'provider':'tmdb','type':'movie','id':'10'})
        row=compare(x,x,{'1':{'verified':False,'correctIdentities':None}})['entries'][0]
        self.assertEqual('a_revoir',row['category']); self.assertIsNone(row['falsePositive'])
    def test_review_distinguishes_gain_regression_false_positive_and_abstention(self):
        correct=self.run_data('accept',{'provider':'tmdb','type':'movie','id':'10'}); abstain=self.run_data()
        wrong=self.run_data('accept',{'provider':'tmdb','type':'movie','id':'11'})
        labels={'1':{'verified':True,'correctIdentities':['tmdb:movie:10']}}
        self.assertEqual('nouvelle_association_correcte',compare(abstain,correct,labels)['entries'][0]['category'])
        self.assertEqual('regression',compare(correct,abstain,labels)['entries'][0]['category'])
        self.assertEqual('faux_positif',compare(abstain,wrong,labels)['entries'][0]['category'])
        labels['1']['abstentionJustified']=True
        self.assertEqual('abstention_justifiee',compare(abstain,abstain,labels)['entries'][0]['category'])
    def test_changed_evidence_or_missing_inputs_are_not_regressions(self):
        a=self.run_data();b=copy.deepcopy(a);b['entries'][0]['entry']['providerEvidenceFingerprint']='changed'
        self.assertEqual('non_evaluable',compare(a,b,{})['entries'][0]['category'])
        a['captureSetHash']=b['captureSetHash']='same-frozen-responses'
        row=compare(a,b,{})['entries'][0]
        self.assertEqual('a_revoir',row['category']); self.assertTrue(row['evidenceChanged'])
        b['datasetHash']='other'
        with self.assertRaises(ValueError): compare(a,b,{})
        b=copy.deepcopy(a);b['completed']=False
        with self.assertRaises(ValueError): compare(a,b,{})

if __name__=='__main__': unittest.main()
